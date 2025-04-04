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
            
            _context = new TestLambdaContext();
            _function = new Function();

            // Configurar as dependências mockadas via reflection
            var s3ClientField = typeof(Function).GetField("_s3Client", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var httpClientField = typeof(Function).GetField("_httpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            s3ClientField?.SetValue(_function, _s3ClientMock.Object);
            httpClientField?.SetValue(_function, httpClient);
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

            // Act
            await _function.FunctionHandler(sqsEvent, _context);

            // Assert
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
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));

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
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));

            // Act & Assert
            await Assert.ThrowsAsync<HttpRequestException>(() => _function.FunctionHandler(sqsEvent, _context));
        }

        private SQSEvent CreateSQSEvent(string key)
        {
            return new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                {
                    new SQSEvent.SQSMessage
                    {
                        Body = $"{{\"Records\":[{{\"s3\":{{\"bucket\":{{\"name\":\"{TEST_BUCKET}\"}},\"object\":{{\"key\":\"{key}\"}}}}}}]}}",
                    }
                }
            };
        }

        private void SetupS3MockForDownload()
        {
            _s3ClientMock.Setup(x => x.GetObjectAsync(It.IsAny<Amazon.S3.Model.GetObjectRequest>(), default))
                        .ReturnsAsync(new GetObjectResponse
                        {
                            ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes("test data"))
                        });
        }

        private void SetupHttpMockForStatusUpdate()
        {
            _httpMessageHandlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", 
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri.ToString().Contains("/status")),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));
        }

        private void SetupHttpMockForMetadataUpdate()
        {
            _httpMessageHandlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", 
                    ItExpr.Is<HttpRequestMessage>(req => !req.RequestUri.ToString().Contains("/status")),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));
        }

        private void VerifyHttpRequestsMade()
        {
            _httpMessageHandlerMock.Protected()
                .Verify("SendAsync", Times.Exactly(2), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
        }
    }
} 