using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.S3;
using Amazon.S3.Model;
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
using Xabe.FFmpeg;

namespace Lambda_FrameSnap_Processor.Tests
{
    public class FunctionTests
    {
        private readonly Mock<IAmazonS3> _s3ClientMock;
        private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
        private readonly TestLambdaContext _context;
        private readonly Function _function;

        private const string TEST_BUCKET = "test-bucket";
        private const string TEST_API_URL = "http://api.example.com";

        public FunctionTests()
        {
            _s3ClientMock = new Mock<IAmazonS3>();
            _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
            var httpClient = new HttpClient(_httpMessageHandlerMock.Object);

            Environment.SetEnvironmentVariable("BUCKET_NAME", TEST_BUCKET);
            Environment.SetEnvironmentVariable("API_BASE_URL", TEST_API_URL);
            Environment.SetEnvironmentVariable("TEMP_DIR", Path.GetTempPath());

            _context = new TestLambdaContext();
            _function = new Function(_s3ClientMock.Object, httpClient);
        }

        [Fact]
        public async Task FunctionHandler_NoRecords_LogsAndReturns()
        {
            // Arrange
            var sqsEvent = new SQSEvent { Records = new List<SQSEvent.SQSMessage>() };

            // Act
            await _function.FunctionHandler(sqsEvent, _context);

            // Assert
            var logger = (TestLambdaLogger)_context.Logger;
            Assert.Contains("Nenhum registro SQS recebido", logger.Buffer.ToString());
        }

        [Fact]
        public async Task ProcessMessageAsync_ZipFile_IgnoresProcessing()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.zip");

            // Act
            await _function.FunctionHandler(sqsEvent, _context);

            // Assert
            var logger = (TestLambdaLogger)_context.Logger;
            Assert.Contains("Ignorando arquivo ZIP", logger.Buffer.ToString());
            _s3ClientMock.Verify(x => x.GetObjectAsync(It.IsAny<Amazon.S3.Model.GetObjectRequest>(), default), Times.Never);
        }

        [Fact]
        public async Task ProcessMessageAsync_ValidVideo_ProcessesSuccessfully()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();
            SetupHttpMockForStatusUpdate();
            SetupHttpMockForMetadataUpdate();

            // Mock FFmpeg
            Environment.SetEnvironmentVariable("TEMP_DIR", Path.GetTempPath());

            // Act
            await _function.FunctionHandler(sqsEvent, _context);

            // Assert
            var logger = (TestLambdaLogger)_context.Logger;
            Assert.Contains("Processando vídeo", logger.Buffer.ToString());

            _s3ClientMock.Verify(x => x.GetObjectAsync(It.IsAny<Amazon.S3.Model.GetObjectRequest>(), default), Times.Once);
            _s3ClientMock.Verify(x => x.PutObjectAsync(It.IsAny<Amazon.S3.Model.PutObjectRequest>(), default), Times.Once);
            VerifyHttpRequestsMade();
        }

        [Fact]
        public async Task ProcessMessageAsync_S3DownloadFails_ThrowsException()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            _s3ClientMock.Setup(x => x.GetObjectAsync(It.IsAny<Amazon.S3.Model.GetObjectRequest>(), default))
                        .ThrowsAsync(new AmazonS3Exception("Download failed"));

            _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

            // Act & Assert
            await Assert.ThrowsAsync<AmazonS3Exception>(() => _function.FunctionHandler(sqsEvent, _context));
        }

        [Fact]
        public async Task ProcessMessageAsync_RedisUpdateFails_ThrowsException()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();

            _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/status")),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Redis update failed"));

            // Act & Assert
            await Assert.ThrowsAsync<HttpRequestException>(() => _function.FunctionHandler(sqsEvent, _context));
        }

        [Fact]
        public async Task ProcessMessageAsync_DynamoUpdateFails_ThrowsException()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();
            SetupHttpMockForStatusUpdate();

            _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => !req.RequestUri!.ToString().Contains("/status")),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("DynamoDB update failed"));

            // Act & Assert
            await Assert.ThrowsAsync<HttpRequestException>(() => _function.FunctionHandler(sqsEvent, _context));
        }       

        [Fact]
        public async Task ProcessMessageAsync_InvalidS3Event_LogsWarning()
        {
            // Arrange
            var sqsEvent = new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                {
                    new SQSEvent.SQSMessage
                    {
                        Body = "{\"Records\": null}"
                    }
                }
            };

            // Act
            await _function.FunctionHandler(sqsEvent, _context);

            // Assert
            var logger = (TestLambdaLogger)_context.Logger;
            Assert.Contains("Registro S3 inválido ou ausente", logger.Buffer.ToString());
        }

        [Fact]
        public async Task ProcessMessageAsync_EmptyS3Event_LogsWarning()
        {
            // Arrange
            var sqsEvent = new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                {
                    new SQSEvent.SQSMessage
                    {
                        Body = "{\"Records\":[]}"
                    }
                }
            };

            // Act
            await _function.FunctionHandler(sqsEvent, _context);

            // Assert
            var logger = (TestLambdaLogger)_context.Logger;
            Assert.Contains("Registro S3 inválido ou ausente", logger.Buffer.ToString());
        }

        private SQSEvent CreateSQSEvent(string fileName)
        {
            var s3Event = new
            {
                Records = new[]
                {
                    new
                    {
                        s3 = new
                        {
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

        private void SetupS3MockForDownload()
        {
            var stream = new MemoryStream(Encoding.UTF8.GetBytes("test data"));
            var response = new GetObjectResponse
            {
                ResponseStream = stream
            };

            _s3ClientMock.Setup(x => x.GetObjectAsync(It.IsAny<Amazon.S3.Model.GetObjectRequest>(), default))
                        .ReturnsAsync(response);
        }

        private void SetupHttpMockForStatusUpdate()
        {
            _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/status")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));
        }

        private void SetupHttpMockForMetadataUpdate()
        {
            _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => !req.RequestUri!.ToString().Contains("/status")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));
        }

        private void VerifyHttpRequestsMade()
        {
            _httpMessageHandlerMock.Protected().Verify(
                "SendAsync",
                Times.Exactly(2),
                ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Put),
                ItExpr.IsAny<CancellationToken>()
            );
        }
    }
}