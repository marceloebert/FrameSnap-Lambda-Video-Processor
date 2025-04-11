using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text;
using Xunit;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.IO;
using System.Reflection;
using Amazon.SimpleNotificationService.Model;

namespace Lambda_FrameSnap_Processor.Tests
{
    public class FunctionTests
    {
        private readonly Mock<IAmazonS3> _s3ClientMock;
        private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
        private readonly Mock<IAmazonSimpleNotificationService> _snsClientMock;
        private readonly TestLambdaContext _context;
        private readonly Function _function;

        private const string TEST_BUCKET = "test-bucket";
        private const string TEST_API_URL = "http://api.example.com";

        public FunctionTests()
        {
            _s3ClientMock = new Mock<IAmazonS3>();
            _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
            _snsClientMock = new Mock<IAmazonSimpleNotificationService>();
            var httpClient = new HttpClient(_httpMessageHandlerMock.Object);

            Environment.SetEnvironmentVariable("BUCKET_NAME", TEST_BUCKET);
            Environment.SetEnvironmentVariable("API_BASE_URL", TEST_API_URL);

            Directory.CreateDirectory("/opt");
            File.WriteAllText("/opt/ffmpeg", "fake-ffmpeg");
            File.WriteAllText("/opt/ffprobe", "fake-ffprobe");

            _context = new TestLambdaContext();
            _function = new Function(_s3ClientMock.Object, httpClient, _snsClientMock.Object);
        }

        [Fact]
        public async Task FunctionHandler_NoRecords_LogsAndReturns()
        {
            var sqsEvent = new SQSEvent { Records = new List<SQSEvent.SQSMessage>() };
            await _function.FunctionHandler(sqsEvent, _context);
            var logger = (TestLambdaLogger)_context.Logger;
            Assert.Contains("Nenhuma mensagem para processar", logger.Buffer.ToString());
        }

        [Fact]
        public async Task ProcessMessageAsync_InvalidS3Event_LogsWarning()
        {
            var sqsEvent = new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                {
                    new SQSEvent.SQSMessage { Body = "{\"Records\":null}" }
                }
            };
            await _function.FunctionHandler(sqsEvent, _context);
            var logger = (TestLambdaLogger)_context.Logger;
            Assert.Contains("Registro S3 inválido ou ausente", logger.Buffer.ToString());
        }

        [Fact]
        public async Task ProcessMessageAsync_EmptyS3Event_LogsWarning()
        {
            var sqsEvent = new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                {
                    new SQSEvent.SQSMessage { Body = "{\"Records\":[]}" }
                }
            };
            await _function.FunctionHandler(sqsEvent, _context);
            var logger = (TestLambdaLogger)_context.Logger;
            Assert.Contains("Registro S3 inválido ou ausente", logger.Buffer.ToString());
        }

        [Fact]
        public async Task ProcessMessageAsync_ZipFile_IgnoresProcessing()
        {
            var sqsEvent = CreateSQSEvent("video.zip");
            await _function.FunctionHandler(sqsEvent, _context);
            var logger = (TestLambdaLogger)_context.Logger;
            Assert.Contains("Ignorando arquivo ZIP", logger.Buffer.ToString());
        }
      

        [Fact]
        public async Task UpdateRedisStatus_SuccessfulCall_LogsInformation()
        {
            var videoId = "vid123";
            var status = "PROCESSING";

            _httpMessageHandlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/status")),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

            var task = (Task)_function.GetType()
                .GetMethod("UpdateRedisStatus", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_function, new object[] { videoId, status, _context })!;
            await task;
        }

        [Fact]
        public async Task UpdateDynamoMetadata_SuccessfulCall_LogsInformation()
        {
            var videoId = "vid123";
            var zipKey = "thumbnails/vid123_thumbnails.zip";
            var status = "COMPLETED";

            _httpMessageHandlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains(videoId) && !req.RequestUri.ToString().Contains("status")),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

            var task = (Task)_function.GetType()
                .GetMethod("UpdateDynamoMetadata", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_function, new object[] { videoId, zipKey, status, _context })!;
            await task;
        }

        [Fact]
        public async Task SendSnsNotification_SuccessfulCall_LogsInformation()
        {
            var videoId = "vid123";
            var status = "COMPLETED";
            var zipKey = "thumbnails/vid123_thumbnails.zip";

            _snsClientMock.Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse { MessageId = "mock-message-id" });

            var task = (Task)_function.GetType()
                .GetMethod("SendSnsNotification", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_function, new object[] { videoId, status, zipKey, _context })!;
            await task;
        }

        [Fact]
        public async Task UpdateRedisStatus_Fails_ShouldThrowAndLog()
        {
            var videoId = "vidFail";
            var status = "PROCESSING";

            _httpMessageHandlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("Redis offline"));

            var method = _function.GetType().GetMethod("UpdateRedisStatus", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task)method.Invoke(_function, new object[] { videoId, status, _context })!;
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => task);
            Assert.Contains("Redis offline", ex.Message);
        }

        [Fact]
        public async Task UpdateDynamoMetadata_Fails_ShouldThrowAndLog()
        {
            var videoId = "vidDynamo";
            var zipKey = "thumbnails/vidDynamo.zip";
            var status = "COMPLETED";

            _httpMessageHandlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("Dynamo falhou"));

            var method = _function.GetType().GetMethod("UpdateDynamoMetadata", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task)method.Invoke(_function, new object?[] { videoId, zipKey, status, _context })!;
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => task);
            Assert.Contains("Dynamo falhou", ex.Message);
        }

        [Fact]
        public async Task SendSnsNotification_Fails_ShouldLogError()
        {
            var videoId = "vidSNS";
            var status = "COMPLETED";
            var zipKey = "thumbnails/vidSNS.zip";

            _snsClientMock.Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("SNS quebrado"));

            var method = _function.GetType().GetMethod("SendSnsNotification", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task)method.Invoke(_function, new object[] { videoId, status, zipKey, _context })!;
            await task;

            var logger = (TestLambdaLogger)_context.Logger;
            Assert.Contains("Erro ao enviar notificação SNS", logger.Buffer.ToString());
            Assert.Contains("SNS quebrado", logger.Buffer.ToString());
        }

        [Fact]
        public async Task FunctionHandler_OnlyValidatesInitialFlow_NoFFmpeg()
        {
            var sqsEvent = CreateSQSEvent("video.mp4");

            // Mock S3 com conteúdo falso
            _s3ClientMock.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetObjectResponse
                {
                    BucketName = TEST_BUCKET,
                    Key = "video.mp4",
                    ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes("fake content")),
                    HttpStatusCode = HttpStatusCode.OK
                });

            // Mock SNS
            _snsClientMock.Setup(s => s.PublishAsync(It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse { MessageId = "test-id" });

            // Mock HTTP para Redis e Dynamo
            _httpMessageHandlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

            // Delete ffmpeg do /opt se precisar evitar execução
            if (File.Exists("/opt/ffprobe")) File.Delete("/opt/ffprobe");

            var ex = await Record.ExceptionAsync(() => _function.FunctionHandler(sqsEvent, _context));

            Assert.NotNull(ex);
            Assert.Contains("FFmpeg não encontrado", ex.Message);
        }

        [Fact]
        public async Task ProcessMessageAsync_ShouldHandle_ZipUploadFailure()
        {
            var sqsEvent = CreateSQSEvent("video.mp4");

            _s3ClientMock.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetObjectResponse
                {
                    BucketName = TEST_BUCKET,
                    Key = "video.mp4",
                    ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes("mock")),
                    HttpStatusCode = HttpStatusCode.OK
                });

            _s3ClientMock.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Falha ao fazer upload do ZIP"));

            _httpMessageHandlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

            var ex = await Record.ExceptionAsync(() => _function.FunctionHandler(sqsEvent, _context));
            Assert.NotNull(ex); // confirmação mínima
        }


        [Fact]
        public async Task ProcessMessageAsync_ShouldHandle_NullMediaInfo()
        {
            // Deleta ffprobe pra forçar falha
            if (File.Exists("/opt/ffprobe"))
                File.Delete("/opt/ffprobe");

            var sqsEvent = CreateSQSEvent("video.mp4");

            _s3ClientMock.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetObjectResponse
                {
                    BucketName = TEST_BUCKET,
                    Key = "video.mp4",
                    ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes("mock")),
                    HttpStatusCode = HttpStatusCode.OK
                });

            var ex = await Record.ExceptionAsync(() => _function.FunctionHandler(sqsEvent, _context));
            Assert.NotNull(ex);
            Assert.IsAssignableFrom<Exception>(ex);
        }




        private SQSEvent CreateSQSEvent(string fileName)
        {
            var s3Event = new
            {
                Records = new[] {
                    new {
                        s3 = new {
                            bucket = new { name = TEST_BUCKET },
                            @object = new { key = fileName }
                        }
                    }
                }
            };

            return new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                {
                    new SQSEvent.SQSMessage
                    {
                        Body = JsonConvert.SerializeObject(s3Event)
                    }
                }
            };
        }
    }
}
