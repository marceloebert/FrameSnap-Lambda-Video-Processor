using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
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
    public class FunctionTests : IDisposable
    {
        private readonly Mock<IAmazonS3> _s3ClientMock;
        private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
        private readonly Mock<IVideoProcessor> _mockVideoProcessor;
        private readonly Mock<IAmazonSimpleNotificationService> _snsClientMock;
        private readonly TestLambdaContext _context;
        private readonly Function _function;
        private readonly string _tempDir;

        private const string TEST_BUCKET = "test-bucket";
        private const string TEST_API_URL = "http://api.example.com";
        private const string TEST_SNS_TOPIC_ARN = "arn:aws:sns:region:account:topic";

        public FunctionTests()
        {
            _s3ClientMock = new Mock<IAmazonS3>();
            _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
            var httpClient = new HttpClient(_httpMessageHandlerMock.Object);
            _mockVideoProcessor = new Mock<IVideoProcessor>();
            _snsClientMock = new Mock<IAmazonSimpleNotificationService>();
            _context = new TestLambdaContext();

            _tempDir = Path.Combine(Path.GetTempPath(), "test_lambda_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_tempDir);

            Environment.SetEnvironmentVariable("BUCKET_NAME", TEST_BUCKET);
            Environment.SetEnvironmentVariable("API_BASE_URL", TEST_API_URL);
            Environment.SetEnvironmentVariable("TEMP_DIR", _tempDir);
            Environment.SetEnvironmentVariable("FFMPEG_PATH", "ffmpeg");
            Environment.SetEnvironmentVariable("SNS_TOPIC_ARN", TEST_SNS_TOPIC_ARN);

            _function = new Function(_s3ClientMock.Object, httpClient, _mockVideoProcessor.Object, _snsClientMock.Object);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch { }
        }

        //[Fact]
        //public async Task FunctionHandler_NoRecords_LogsAndReturns()
        //{
        //    // Arrange
        //    var sqsEvent = new SQSEvent { Records = new List<SQSEvent.SQSMessage>() };

        //    // Act
        //    await _function.FunctionHandler(sqsEvent, _context);

        //    // Assert
        //    var logger = (TestLambdaLogger)_context.Logger;
        //    Assert.Contains("Nenhum registro SQS recebido", logger.Buffer.ToString());
        //}

        //[Fact]
        //public async Task ProcessMessageAsync_ZipFile_IgnoresProcessing()
        //{
        //    // Arrange
        //    var sqsEvent = CreateSQSEvent("test.zip");

        //    // Act
        //    await _function.FunctionHandler(sqsEvent, _context);

        //    // Assert
        //    var logger = (TestLambdaLogger)_context.Logger;
        //    Assert.Contains("Ignorando arquivo ZIP", logger.Buffer.ToString());
        //    _s3ClientMock.Verify(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), default), Times.Never);
        //}

      


        //[Fact]
        //public async Task ProcessMessageAsync_RedisUpdateFails_ThrowsException()
        //{
        //    // Arrange
        //    var sqsEvent = CreateSQSEvent("test.mp4");
        //    SetupS3MockForDownload();

        //    _httpMessageHandlerMock.Protected()
        //    .Setup<Task<HttpResponseMessage>>("SendAsync",
        //        ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/status")),
        //        ItExpr.IsAny<CancellationToken>())
        //    .ThrowsAsync(new HttpRequestException("Redis update failed"));

        //    // Act & Assert
        //    await Assert.ThrowsAsync<HttpRequestException>(() => _function.FunctionHandler(sqsEvent, _context));
        //}

        //[Fact]
        //public async Task ProcessMessageAsync_DynamoUpdateFails_ThrowsException()
        //{
        //    // Arrange
        //    var sqsEvent = CreateSQSEvent("test.mp4");
        //    SetupS3MockForDownload();
        //    SetupHttpMockForStatusUpdate();

        //    _httpMessageHandlerMock.Protected()
        //    .Setup<Task<HttpResponseMessage>>("SendAsync",
        //        ItExpr.Is<HttpRequestMessage>(req => !req.RequestUri!.ToString().Contains("/status")),
        //        ItExpr.IsAny<CancellationToken>())
        //    .ThrowsAsync(new HttpRequestException("DynamoDB update failed"));

        //    // Act & Assert
        //    await Assert.ThrowsAsync<HttpRequestException>(() => _function.FunctionHandler(sqsEvent, _context));
        //}

        //[Fact]
        //public async Task ProcessMessageAsync_InvalidS3Event_LogsWarning()
        //{
        //    // Arrange
        //    var sqsEvent = new SQSEvent
        //    {
        //        Records = new List<SQSEvent.SQSMessage>
        //        {
        //            new SQSEvent.SQSMessage
        //            {
        //                Body = "{\"Records\": null}"
        //            }
        //        }
        //    };

        //    // Act
        //    await _function.FunctionHandler(sqsEvent, _context);

        //    // Assert
        //    var logger = (TestLambdaLogger)_context.Logger;
        //    Assert.Contains("Registro S3 inválido ou ausente", logger.Buffer.ToString());
        //}

        //[Fact]
        //public async Task ProcessMessageAsync_EmptyS3Event_LogsWarning()
        //{
        //    // Arrange
        //    var sqsEvent = new SQSEvent
        //    {
        //        Records = new List<SQSEvent.SQSMessage>
        //        {
        //            new SQSEvent.SQSMessage
        //            {
        //                Body = "{\"Records\":[]}"
        //            }
        //        }
        //    };

        //    // Act
        //    await _function.FunctionHandler(sqsEvent, _context);

        //    // Assert
        //    var logger = (TestLambdaLogger)_context.Logger;
        //    Assert.Contains("Registro S3 inválido ou ausente", logger.Buffer.ToString());
        //}

      

       

        //[Fact]
        //public async Task ProcessMessageAsync_InvalidVideoFormat_LogsWarning()
        //{
        //    // Arrange
        //    var sqsEvent = CreateSQSEvent("test.txt");
        //    SetupS3MockForDownload();

        //    // Act
        //    await _function.FunctionHandler(sqsEvent, _context);

        //    // Assert
        //    var logger = (TestLambdaLogger)_context.Logger;
        //    Assert.Contains("Formato de vídeo não suportado", logger.Buffer.ToString());
        //}

        //[Fact]
        //public async Task ProcessMessageAsync_HttpClientFails_ThrowsException()
        //{
        //    // Arrange
        //    var sqsEvent = CreateSQSEvent("test.mp4");
        //    SetupS3MockForDownload();

        //    _httpMessageHandlerMock.Protected()
        //    .Setup<Task<HttpResponseMessage>>("SendAsync",
        //        ItExpr.IsAny<HttpRequestMessage>(),
        //        ItExpr.IsAny<CancellationToken>())
        //    .ThrowsAsync(new HttpRequestException("HTTP request failed"));

        //    // Act & Assert
        //    await Assert.ThrowsAsync<HttpRequestException>(() => _function.FunctionHandler(sqsEvent, _context));
        //}

       

        //[Fact]
        //public async Task ProcessMessageAsync_StatusUpdateFailure_ThrowsException()
        //{
        //    // Arrange
        //    var sqsEvent = CreateSQSEvent("test.mp4");
        //    SetupS3MockForDownload();

        //    _httpMessageHandlerMock.Protected()
        //        .Setup<Task<HttpResponseMessage>>("SendAsync",
        //            ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/status")),
        //            ItExpr.IsAny<CancellationToken>())
        //        .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        //    // Act & Assert
        //    var exception = await Assert.ThrowsAsync<HttpRequestException>(
        //        () => _function.FunctionHandler(sqsEvent, _context));
        //    Assert.Contains("Status update failed", exception.Message);
        //}

        //[Fact]
        //public async Task ProcessMessageAsync_MetadataUpdateFailure_ThrowsException()
        //{
        //    // Arrange
        //    var sqsEvent = CreateSQSEvent("test.mp4");
        //    SetupS3MockForDownload();
        //    SetupHttpMockForStatusUpdate();

        //    _httpMessageHandlerMock.Protected()
        //        .Setup<Task<HttpResponseMessage>>("SendAsync",
        //            ItExpr.Is<HttpRequestMessage>(req => !req.RequestUri!.ToString().Contains("/status")),
        //            ItExpr.IsAny<CancellationToken>())
        //        .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        //    // Act & Assert
        //    var exception = await Assert.ThrowsAsync<HttpRequestException>(
        //        () => _function.FunctionHandler(sqsEvent, _context));
        //    Assert.Contains("Metadata update failed", exception.Message);
        //}        

       

        //[Fact]
        //public async Task ProcessMessageAsync_InvalidHttpResponse_ThrowsException()
        //{
        //    // Arrange
        //    var sqsEvent = CreateSQSEvent("test.mp4");
        //    SetupS3MockForDownload();

        //    _httpMessageHandlerMock.Protected()
        //        .Setup<Task<HttpResponseMessage>>("SendAsync",
        //            ItExpr.IsAny<HttpRequestMessage>(),
        //            ItExpr.IsAny<CancellationToken>())
        //        .ThrowsAsync(new HttpRequestException("Network error"));

        //    // Act & Assert
        //    await Assert.ThrowsAsync<HttpRequestException>(
        //        () => _function.FunctionHandler(sqsEvent, _context));
        //}

      
        //[Fact]
        //public async Task ProcessMessageAsync_OutputDirectoryCreationFails_ThrowsException()
        //{
        //    // Arrange
        //    var sqsEvent = CreateSQSEvent("test.mp4");
        //    SetupS3MockForDownload();
        //    SetupHttpMockForStatusUpdate();
        //    SetupHttpMockForMetadataUpdate();

        //    // Criar um arquivo com o mesmo nome do diretório que queremos criar
        //    var outputDir = Path.Combine(_tempDir, "images");
        //    File.WriteAllText(outputDir, "test");

        //    // Act & Assert
        //    var exception = await Assert.ThrowsAsync<IOException>(
        //        () => _function.FunctionHandler(sqsEvent, _context));
        //    Assert.Contains("diretório", exception.Message.ToLower());
        //}

        //[Fact]
        //public async Task ProcessMessageAsync_CorruptedVideoFile_ThrowsException()
        //{
        //    // Arrange
        //    var sqsEvent = CreateSQSEvent("test.mp4");
        //    SetupS3MockForDownload();
        //    SetupHttpMockForStatusUpdate();
        //    SetupHttpMockForMetadataUpdate();

        //    // Criar um arquivo de vídeo corrompido
        //    var videoPath = Path.Combine(_tempDir, "test.mp4");
        //    await File.WriteAllBytesAsync(videoPath, new byte[] { 0x00, 0x01, 0x02, 0x03 });

        //    // Act & Assert
        //    var exception = await Assert.ThrowsAsync<Exception>(
        //        () => _function.FunctionHandler(sqsEvent, _context));
        //    Assert.Contains("metadata", exception.Message.ToLower());
        //}

        //[Fact]
        //public async Task ProcessMessageAsync_ZeroDurationVideo_ThrowsException()
        //{
        //    // Arrange
        //    var sqsEvent = CreateSQSEvent("test.mp4");
        //    SetupS3MockForDownload();
        //    SetupHttpMockForStatusUpdate();
        //    SetupHttpMockForMetadataUpdate();

        //    // Mock do FFmpeg para retornar duração zero
        //    var mockVideoStream = new Mock<IVideoStream>();
        //    mockVideoStream.Setup(x => x.Width).Returns(1920);
        //    mockVideoStream.Setup(x => x.Height).Returns(1080);

        //    var mockMediaInfo = new Mock<IMediaInfo>();
        //    mockMediaInfo.Setup(x => x.Duration).Returns(TimeSpan.Zero);
        //    mockMediaInfo.Setup(x => x.VideoStreams).Returns(new List<IVideoStream> { mockVideoStream.Object });

        //    // Act & Assert
        //    var exception = await Assert.ThrowsAsync<Exception>(
        //        () => _function.FunctionHandler(sqsEvent, _context));
        //    Assert.Contains("duração", exception.Message.ToLower());
        //}

     

        //[Fact]
        //public async Task ProcessMessageAsync_InvalidVideoMetadata_ThrowsException()
        //{
        //    // Arrange
        //    var sqsEvent = CreateSQSEvent("test.mp4");
        //    SetupS3MockForDownload();
        //    SetupHttpMockForStatusUpdate();
        //    SetupHttpMockForMetadataUpdate();

        //    // Mock do FFmpeg para retornar metadata inválida
        //    var mockVideoStream = new Mock<IVideoStream>();
        //    mockVideoStream.Setup(x => x.Width).Returns(1920);
        //    mockVideoStream.Setup(x => x.Height).Returns(1080);

        //    var mockMediaInfo = new Mock<IMediaInfo>();
        //    mockMediaInfo.Setup(x => x.Duration).Returns(TimeSpan.FromMinutes(-1));
        //    mockMediaInfo.Setup(x => x.VideoStreams).Returns(new List<IVideoStream> { mockVideoStream.Object });

        //    // Act & Assert
        //    var exception = await Assert.ThrowsAsync<Exception>(
        //        () => _function.FunctionHandler(sqsEvent, _context));
        //    Assert.Contains("metadata", exception.Message.ToLower());
        //}        

       

        private void SetupSNSMockForPublish()
        {
            _snsClientMock.Setup(x => x.PublishAsync(
                It.IsAny<PublishRequest>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PublishResponse());
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

        private SQSEvent.SQSMessage CreateSQSMessage(string fileName)
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

            return new SQSEvent.SQSMessage
            {
                Body = JsonConvert.SerializeObject(s3Event)
            };
        }

        private void SetupS3MockForDownload()
        {
            var stream = new MemoryStream(new byte[1024]); // 1KB dummy video
            var response = new GetObjectResponse
            {
                ResponseStream = stream
            };

            _s3ClientMock.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), default))
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
    }
}