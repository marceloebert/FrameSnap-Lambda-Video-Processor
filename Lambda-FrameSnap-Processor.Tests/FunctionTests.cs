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
            _s3ClientMock.Verify(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), default), Times.Never);
        }

      

        [Fact]
        public async Task ProcessMessageAsync_S3DownloadFails_ThrowsException()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            _s3ClientMock.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), default))
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

        [Fact]
        public async Task ProcessMessageAsync_FFmpegFails_ThrowsException()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();
            Environment.SetEnvironmentVariable("FFMPEG_PATH", "invalid_path");

            // Act & Assert
            await Assert.ThrowsAsync<Exception>(() => _function.FunctionHandler(sqsEvent, _context));
        }

        [Fact]
        public async Task ProcessMessageAsync_InvalidJson_LogsWarning()
        {
            // Arrange
            var sqsEvent = new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                {
                    new SQSEvent.SQSMessage
                    {
                        Body = "invalid json"
                    }
                }
            };

            // Act
            await _function.FunctionHandler(sqsEvent, _context);

            // Assert
            var logger = (TestLambdaLogger)_context.Logger;
            Assert.Contains("Erro ao processar mensagem SQS", logger.Buffer.ToString());
        }

        [Fact]
        public async Task ProcessMessageAsync_S3UploadFails_ThrowsException()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();
            SetupHttpMockForStatusUpdate();

            _s3ClientMock.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), default))
                        .ThrowsAsync(new AmazonS3Exception("Upload failed"));

            // Act & Assert
            await Assert.ThrowsAsync<AmazonS3Exception>(() => _function.FunctionHandler(sqsEvent, _context));
        }

        [Fact]
        public async Task ProcessMessageAsync_InvalidVideoFormat_LogsWarning()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.txt");
            SetupS3MockForDownload();

            // Act
            await _function.FunctionHandler(sqsEvent, _context);

            // Assert
            var logger = (TestLambdaLogger)_context.Logger;
            Assert.Contains("Formato de vídeo não suportado", logger.Buffer.ToString());
        }

        [Fact]
        public async Task ProcessMessageAsync_HttpClientFails_ThrowsException()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();

            _httpMessageHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("HTTP request failed"));

            // Act & Assert
            await Assert.ThrowsAsync<HttpRequestException>(() => _function.FunctionHandler(sqsEvent, _context));
        }

       

        [Fact]
        public async Task ProcessMessageAsync_VideoProcessing_GeneratesCorrectThumbnails()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();
            SetupHttpMockForStatusUpdate();
            SetupHttpMockForMetadataUpdate();

            var tempDir = Path.Combine(Path.GetTempPath(), "test_thumbnails");
            Environment.SetEnvironmentVariable("TEMP_DIR", tempDir);

            try
            {
                // Act
                await _function.FunctionHandler(sqsEvent, _context);

                // Assert
                _s3ClientMock.Verify(x => x.PutObjectAsync(
                    It.Is<PutObjectRequest>(r => r.Key.StartsWith("thumbnails/")),
                    It.IsAny<CancellationToken>()),
                    Times.Once);

                var logger = (TestLambdaLogger)_context.Logger;
                Assert.Contains("Thumbnails gerados com sucesso", logger.Buffer.ToString());
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }       

        [Fact]
        public async Task ProcessMessageAsync_StatusUpdateFailure_ThrowsException()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();

            _httpMessageHandlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/status")),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));

            // Act & Assert
            var exception = await Assert.ThrowsAsync<HttpRequestException>(
                () => _function.FunctionHandler(sqsEvent, _context));
            Assert.Contains("Status update failed", exception.Message);
        }

        [Fact]
        public async Task ProcessMessageAsync_MetadataUpdateFailure_ThrowsException()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();
            SetupHttpMockForStatusUpdate();

            _httpMessageHandlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => !req.RequestUri!.ToString().Contains("/status")),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));

            // Act & Assert
            var exception = await Assert.ThrowsAsync<HttpRequestException>(
                () => _function.FunctionHandler(sqsEvent, _context));
            Assert.Contains("Metadata update failed", exception.Message);
        }        

        [Fact]
        public async Task ProcessMessageAsync_InvalidTempDir_ThrowsException()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();
            
            Environment.SetEnvironmentVariable("TEMP_DIR", "Z:\\invalid\\path");

            // Act & Assert
            await Assert.ThrowsAsync<DirectoryNotFoundException>(
                () => _function.FunctionHandler(sqsEvent, _context));
        }

      
        [Fact]
        public async Task ProcessMessageAsync_MultipleRecords_ProcessesAll()
        {
            // Arrange
            var sqsEvent = new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                {
                    CreateSQSMessage("video1.mp4"),
                    CreateSQSMessage("video2.mp4")
                }
            };

            SetupS3MockForDownload();
            SetupHttpMockForStatusUpdate();
            SetupHttpMockForMetadataUpdate();

            // Act
            await _function.FunctionHandler(sqsEvent, _context);

            // Assert
            _s3ClientMock.Verify(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), default),
                Times.Exactly(2));
        }

        [Fact]
        public async Task ProcessMessageAsync_LargeVideoFile_HandlesSuccessfully()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("large_video.mp4");
            var largeStream = new MemoryStream(new byte[1024 * 1024]); // 1MB
            var response = new GetObjectResponse
            {
                ResponseStream = largeStream
            };

            _s3ClientMock.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), default))
                        .ReturnsAsync(response);

            SetupHttpMockForStatusUpdate();
            SetupHttpMockForMetadataUpdate();

            // Act
            await _function.FunctionHandler(sqsEvent, _context);

            // Assert
            var logger = (TestLambdaLogger)_context.Logger;
            Assert.Contains("Processando vídeo", logger.Buffer.ToString());
            Assert.Contains("Thumbnails gerados com sucesso", logger.Buffer.ToString());
        }

        [Fact]
        public async Task ProcessMessageAsync_NullBucketName_ThrowsException()
        {
            // Arrange
            Environment.SetEnvironmentVariable("BUCKET_NAME", null);
            var sqsEvent = CreateSQSEvent("test.mp4");

            // Act & Assert
            var exception = await Assert.ThrowsAsync<Exception>(
                () => _function.FunctionHandler(sqsEvent, _context));
            Assert.Contains("BUCKET_NAME not set", exception.Message);
        }

        [Fact]
        public async Task ProcessMessageAsync_NullApiUrl_ThrowsException()
        {
            // Arrange
            Environment.SetEnvironmentVariable("API_BASE_URL", null);
            var sqsEvent = CreateSQSEvent("test.mp4");

            // Act & Assert
            var exception = await Assert.ThrowsAsync<Exception>(
                () => _function.FunctionHandler(sqsEvent, _context));
            Assert.Contains("API_BASE_URL not set", exception.Message);
        }

        [Fact]
        public async Task ProcessMessageAsync_InvalidS3Response_ThrowsException()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            _s3ClientMock.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), default))
                        .ThrowsAsync(new AmazonS3Exception("Invalid response"));

            // Act & Assert
            await Assert.ThrowsAsync<AmazonS3Exception>(
                () => _function.FunctionHandler(sqsEvent, _context));
        }

        [Fact]
        public async Task ProcessMessageAsync_InvalidHttpResponse_ThrowsException()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();

            _httpMessageHandlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("Network error"));

            // Act & Assert
            await Assert.ThrowsAsync<HttpRequestException>(
                () => _function.FunctionHandler(sqsEvent, _context));
        }

        [Fact]
        public async Task ProcessMessageAsync_FFmpegDownloadFails_ThrowsException()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            Environment.SetEnvironmentVariable("FFMPEG_PATH", "invalid_path");
            SetupS3MockForDownload();

            // Act & Assert
            var exception = await Assert.ThrowsAsync<Exception>(
                () => _function.FunctionHandler(sqsEvent, _context));
            Assert.Contains("FFmpeg", exception.Message);
        }

        [Fact]
        public async Task ProcessMessageAsync_OutputDirectoryCreationFails_ThrowsException()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();
            SetupHttpMockForStatusUpdate();
            SetupHttpMockForMetadataUpdate();

            // Criar um arquivo com o mesmo nome do diretório que queremos criar
            var outputDir = Path.Combine(_tempDir, "images");
            File.WriteAllText(outputDir, "test");

            // Act & Assert
            var exception = await Assert.ThrowsAsync<IOException>(
                () => _function.FunctionHandler(sqsEvent, _context));
            Assert.Contains("diretório", exception.Message.ToLower());
        }

        [Fact]
        public async Task ProcessMessageAsync_CorruptedVideoFile_ThrowsException()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();
            SetupHttpMockForStatusUpdate();
            SetupHttpMockForMetadataUpdate();

            // Criar um arquivo de vídeo corrompido
            var videoPath = Path.Combine(_tempDir, "test.mp4");
            await File.WriteAllBytesAsync(videoPath, new byte[] { 0x00, 0x01, 0x02, 0x03 });

            // Act & Assert
            var exception = await Assert.ThrowsAsync<Exception>(
                () => _function.FunctionHandler(sqsEvent, _context));
            Assert.Contains("metadata", exception.Message.ToLower());
        }

        [Fact]
        public async Task ProcessMessageAsync_ZeroDurationVideo_ThrowsException()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();
            SetupHttpMockForStatusUpdate();
            SetupHttpMockForMetadataUpdate();

            // Mock do FFmpeg para retornar duração zero
            var mockVideoStream = new Mock<IVideoStream>();
            mockVideoStream.Setup(x => x.Width).Returns(1920);
            mockVideoStream.Setup(x => x.Height).Returns(1080);

            var mockMediaInfo = new Mock<IMediaInfo>();
            mockMediaInfo.Setup(x => x.Duration).Returns(TimeSpan.Zero);
            mockMediaInfo.Setup(x => x.VideoStreams).Returns(new List<IVideoStream> { mockVideoStream.Object });

            // Act & Assert
            var exception = await Assert.ThrowsAsync<Exception>(
                () => _function.FunctionHandler(sqsEvent, _context));
            Assert.Contains("duração", exception.Message.ToLower());
        }

     

        [Fact]
        public async Task ProcessMessageAsync_EnvironmentVariablesNotSet_ThrowsException()
        {
            // Arrange
            Environment.SetEnvironmentVariable("BUCKET_NAME", null);
            Environment.SetEnvironmentVariable("API_BASE_URL", null);
            var sqsEvent = CreateSQSEvent("test.mp4");

            // Act & Assert
            var exception = await Assert.ThrowsAsync<Exception>(
                () => _function.FunctionHandler(sqsEvent, _context));
            Assert.Contains("variável de ambiente", exception.Message.ToLower());
        }
       

        [Fact]
        public async Task ProcessMessageAsync_ThumbnailUploadFails_ThrowsException()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();
            SetupHttpMockForStatusUpdate();
            SetupHttpMockForMetadataUpdate();

            // Mock do FFmpeg
            var mockVideoStream = new Mock<IVideoStream>();
            mockVideoStream.Setup(x => x.Width).Returns(1920);
            mockVideoStream.Setup(x => x.Height).Returns(1080);

            var mockMediaInfo = new Mock<IMediaInfo>();
            mockMediaInfo.Setup(x => x.Duration).Returns(TimeSpan.FromMinutes(5));
            mockMediaInfo.Setup(x => x.VideoStreams).Returns(new List<IVideoStream> { mockVideoStream.Object });

            // Configurar o mock do S3 para falhar no upload
            _s3ClientMock.Setup(x => x.PutObjectAsync(
                It.Is<PutObjectRequest>(r => r.Key.StartsWith("thumbnails/")),
                It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonS3Exception("Upload failed"));

            // Act & Assert
            var exception = await Assert.ThrowsAsync<AmazonS3Exception>(
                () => _function.FunctionHandler(sqsEvent, _context));
            Assert.Contains("upload", exception.Message.ToLower());
        }

        [Fact]
        public async Task ProcessMessageAsync_ConcurrentProcessing_Success()
        {
            // Arrange
            var sqsEvent = new SQSEvent
            {
                Records = new List<SQSEvent.SQSMessage>
                {
                    CreateSQSMessage("video1.mp4"),
                    CreateSQSMessage("video2.mp4"),
                    CreateSQSMessage("video3.mp4")
                }
            };

            SetupS3MockForDownload();
            SetupHttpMockForStatusUpdate();
            SetupHttpMockForMetadataUpdate();

            // Mock do FFmpeg para cada vídeo
            var mockVideoStream = new Mock<IVideoStream>();
            mockVideoStream.Setup(x => x.Width).Returns(1920);
            mockVideoStream.Setup(x => x.Height).Returns(1080);

            var mockMediaInfo = new Mock<IMediaInfo>();
            mockMediaInfo.Setup(x => x.Duration).Returns(TimeSpan.FromMinutes(5));
            mockMediaInfo.Setup(x => x.VideoStreams).Returns(new List<IVideoStream> { mockVideoStream.Object });

            // Act
            await _function.FunctionHandler(sqsEvent, _context);

            // Assert
            _s3ClientMock.Verify(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), default),
                Times.Exactly(3));
            _s3ClientMock.Verify(x => x.PutObjectAsync(
                It.Is<PutObjectRequest>(r => r.Key.StartsWith("thumbnails/")),
                It.IsAny<CancellationToken>()),
                Times.Exactly(45)); // 3 vídeos * 15 thumbnails cada
        }

      

        [Fact]
        public async Task ProcessMessageAsync_StatusUpdateRetry_Success()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();

            // Mock do FFmpeg
            var mockVideoStream = new Mock<IVideoStream>();
            mockVideoStream.Setup(x => x.Width).Returns(1920);
            mockVideoStream.Setup(x => x.Height).Returns(1080);

            var mockMediaInfo = new Mock<IMediaInfo>();
            mockMediaInfo.Setup(x => x.Duration).Returns(TimeSpan.FromMinutes(5));
            mockMediaInfo.Setup(x => x.VideoStreams).Returns(new List<IVideoStream> { mockVideoStream.Object });

            // Configurar o mock para falhar na primeira tentativa e suceder na segunda
            var attempts = 0;
            _httpMessageHandlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/status")),
                    ItExpr.IsAny<CancellationToken>())
                .Returns(() =>
                {
                    attempts++;
                    if (attempts == 1)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                });

            SetupHttpMockForMetadataUpdate();

            // Act
            await _function.FunctionHandler(sqsEvent, _context);

            // Assert
            Assert.Equal(2, attempts);
        }      

        [Fact]
        public async Task ProcessMessageAsync_InvalidApiUrl_ThrowsException()
        {
            // Arrange
            Environment.SetEnvironmentVariable("API_BASE_URL", "");
            var sqsEvent = CreateSQSEvent("test.mp4");

            // Act & Assert
            var exception = await Assert.ThrowsAsync<Exception>(
                () => _function.FunctionHandler(sqsEvent, _context));
            Assert.Contains("api", exception.Message.ToLower());
        }    

       

        [Fact]
        public async Task ProcessMessageAsync_ThumbnailDirectoryCleanup_Success()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();
            SetupHttpMockForStatusUpdate();
            SetupHttpMockForMetadataUpdate();

            // Mock do FFmpeg
            var mockVideoStream = new Mock<IVideoStream>();
            mockVideoStream.Setup(x => x.Width).Returns(1920);
            mockVideoStream.Setup(x => x.Height).Returns(1080);

            var mockMediaInfo = new Mock<IMediaInfo>();
            mockMediaInfo.Setup(x => x.Duration).Returns(TimeSpan.FromMinutes(5));
            mockMediaInfo.Setup(x => x.VideoStreams).Returns(new List<IVideoStream> { mockVideoStream.Object });

            var outputDir = Path.Combine(_tempDir, "images");
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(Path.Combine(outputDir, "test.txt"), "test");

            // Act
            await _function.FunctionHandler(sqsEvent, _context);

            // Assert
            Assert.False(Directory.Exists(outputDir));
        }

        [Fact]
        public async Task ProcessMessageAsync_InvalidVideoMetadata_ThrowsException()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();
            SetupHttpMockForStatusUpdate();
            SetupHttpMockForMetadataUpdate();

            // Mock do FFmpeg para retornar metadata inválida
            var mockVideoStream = new Mock<IVideoStream>();
            mockVideoStream.Setup(x => x.Width).Returns(1920);
            mockVideoStream.Setup(x => x.Height).Returns(1080);

            var mockMediaInfo = new Mock<IMediaInfo>();
            mockMediaInfo.Setup(x => x.Duration).Returns(TimeSpan.FromMinutes(-1));
            mockMediaInfo.Setup(x => x.VideoStreams).Returns(new List<IVideoStream> { mockVideoStream.Object });

            // Act & Assert
            var exception = await Assert.ThrowsAsync<Exception>(
                () => _function.FunctionHandler(sqsEvent, _context));
            Assert.Contains("metadata", exception.Message.ToLower());
        }        

        [Fact]
        public async Task ProcessMessageAsync_SNSFailure_ContinuesExecution()
        {
            // Arrange
            var sqsEvent = CreateSQSEvent("test.mp4");
            SetupS3MockForDownload();
            SetupHttpMockForStatusUpdate();
            SetupHttpMockForMetadataUpdate();

            _snsClientMock.Setup(x => x.PublishAsync(
                It.IsAny<PublishRequest>(),
                It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AmazonSimpleNotificationServiceException("SNS error"));

            // Act
            await _function.FunctionHandler(sqsEvent, _context);

            // Assert
            var logger = (TestLambdaLogger)_context.Logger;
            Assert.Contains("Erro ao enviar notificação SNS", logger.Buffer.ToString());
        }

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