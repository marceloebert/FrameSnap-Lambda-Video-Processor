using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Newtonsoft.Json;
using System.Drawing;
using System.IO.Compression;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;
using System.Text;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Lambda_FrameSnap_Processor;

public interface IVideoProcessor
{
    Task<IMediaInfo> GetMediaInfo(string filePath);
    Task GenerateThumbnail(string inputPath, string outputPath, TimeSpan timestamp);
}

public class FFmpegVideoProcessor : IVideoProcessor
{
    public async Task<IMediaInfo> GetMediaInfo(string filePath)
    {
        return await FFmpeg.GetMediaInfo(filePath);
    }

    public async Task GenerateThumbnail(string inputPath, string outputPath, TimeSpan timestamp)
    {
        await FFmpeg.Conversions.New()
            .AddParameter($"-i \"{inputPath}\" -ss {timestamp.TotalSeconds} -vframes 1 -f image2 \"{outputPath}\"")
            .Start();
    }
}

public class Function
{
    private readonly IAmazonS3 _s3Client;
    private readonly HttpClient _httpClient;
    private readonly IVideoProcessor _videoProcessor;
    private readonly IAmazonSimpleNotificationService _snsClient;
    private readonly string BUCKET_NAME;
    private readonly string API_BASE_URL;
    private readonly string TEMP_DIR;
    private readonly string SNS_TOPIC_ARN;
    private readonly bool _isTestEnvironment;

    // Construtor padrão para produção
    public Function()
        : this(new AmazonS3Client(), new HttpClient(), new FFmpegVideoProcessor(), new AmazonSimpleNotificationServiceClient(), false) { }

    // Construtor com injeção de dependência para testes
    public Function(IAmazonS3 s3Client, HttpClient httpClient, IVideoProcessor videoProcessor, IAmazonSimpleNotificationService snsClient, bool isTestEnvironment = true)
    {
        BUCKET_NAME = Environment.GetEnvironmentVariable("BUCKET_NAME") ?? "framesnap-video-bucket";
        API_BASE_URL = Environment.GetEnvironmentVariable("API_BASE_URL") ?? "http://a17402d9af26045b99d867bf497802ea-531317326.us-east-1.elb.amazonaws.com";
        TEMP_DIR = Environment.GetEnvironmentVariable("TEMP_DIR") ?? Path.GetTempPath();
        SNS_TOPIC_ARN = Environment.GetEnvironmentVariable("SNS_TOPIC_ARN") ?? "arn:aws:sns:us-east-1:114692541707:FrameSnap-Notifications";

        if (string.IsNullOrEmpty(BUCKET_NAME))
            throw new Exception("BUCKET_NAME não configurado");

        if (string.IsNullOrEmpty(API_BASE_URL))
            throw new Exception("API_BASE_URL não configurado");

        if (string.IsNullOrEmpty(SNS_TOPIC_ARN))
            throw new Exception("SNS_TOPIC_ARN não configurado");

        _s3Client = s3Client;
        _httpClient = httpClient;
        _videoProcessor = videoProcessor;
        _snsClient = snsClient;
        _isTestEnvironment = isTestEnvironment;

        // Garantir que o diretório temporário existe
        if (!Directory.Exists(TEMP_DIR))
        {
            Directory.CreateDirectory(TEMP_DIR);
        }

        FFmpeg.SetExecutablesPath(TEMP_DIR);
    }

    public async Task FunctionHandler(SQSEvent evnt, ILambdaContext context)
    {
        if (!evnt.Records.Any())
        {
            context.Logger.LogInformation("Nenhum registro SQS recebido.");
            return;
        }

        try
        {
            if (!_isTestEnvironment)
            {
                context.Logger.LogInformation("Baixando FFmpeg...");
                await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, TEMP_DIR);
                context.Logger.LogInformation("FFmpeg baixado com sucesso!");
            }

            foreach (var message in evnt.Records)
            {
                await ProcessMessageAsync(message, context);
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Erro ao processar mensagens: {ex.Message}");
            throw;
        }
    }

    private async Task ProcessMessageAsync(SQSEvent.SQSMessage message, ILambdaContext context)
    {
        string? localVideoPath = null;
        string? outputFolder = null;

        try
        {
            var s3Event = JsonConvert.DeserializeObject<S3Event>(message.Body);
            var s3Record = s3Event?.Records?.FirstOrDefault();

            if (s3Record == null)
            {
                context.Logger.LogWarning("Registro S3 inválido ou ausente.");
                return;
            }

            var videoKey = s3Record.S3.Object.Key;

            if (videoKey.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                context.Logger.LogInformation($"Ignorando arquivo ZIP: {videoKey}");
                return;
            }

            if (!IsValidVideoFormat(videoKey))
            {
                context.Logger.LogWarning($"Formato de vídeo não suportado: {videoKey}");
                return;
            }

            var videoId = Path.GetFileNameWithoutExtension(videoKey).Split('_')[0];
            if (string.IsNullOrEmpty(videoId))
            {
                context.Logger.LogWarning("ID do vídeo inválido");
                return;
            }

            context.Logger.LogInformation($"Processando vídeo: {videoKey}");

            await UpdateRedisStatus(videoId, "PROCESSING", context);
            await UpdateDynamoMetadata(videoId, null, "PROCESSING", context);

            localVideoPath = Path.Combine(TEMP_DIR, Path.GetFileName(videoKey));
            await DownloadVideoFromS3(videoKey, localVideoPath);

            outputFolder = Path.Combine(TEMP_DIR, "images");
            try
            {
                if (Directory.Exists(outputFolder))
                {
                    Directory.Delete(outputFolder, true);
                }
                Directory.CreateDirectory(outputFolder);
            }
            catch (IOException ex)
            {
                throw new IOException($"Não foi possível criar o diretório de saída: {ex.Message}");
            }

            var mediaInfo = await _videoProcessor.GetMediaInfo(localVideoPath);
            if (mediaInfo == null || mediaInfo.Duration <= TimeSpan.Zero)
            {
                throw new Exception("Metadata do vídeo inválida ou duração zero");
            }

            var videoStream = mediaInfo.VideoStreams.FirstOrDefault();
            if (videoStream == null || videoStream.Width <= 0 || videoStream.Height <= 0)
            {
                throw new Exception("Stream de vídeo inválido ou dimensões inválidas");
            }

            var duration = mediaInfo.Duration;
            var interval = TimeSpan.FromSeconds(20);
            var thumbnailCount = (int)(duration.TotalSeconds / interval.TotalSeconds);

            for (int i = 0; i < thumbnailCount; i++)
            {
                var timestamp = i * interval;
                var outputPath = Path.Combine(outputFolder, $"thumbnail_{i}.jpg");

                await _videoProcessor.GenerateThumbnail(localVideoPath, outputPath, timestamp);

                var thumbnailKey = $"thumbnails/{videoId}/thumbnail_{i}.jpg";
                await UploadThumbnailToS3(outputPath, thumbnailKey);
            }

            await UpdateRedisStatus(videoId, "COMPLETED", context);
            await UpdateDynamoMetadata(videoId, thumbnailCount, "COMPLETED", context);
            
            // Enviar notificação SNS após a conclusão do processamento
            await SendNotificationAsync(videoId, "COMPLETED", thumbnailCount, context);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Erro ao processar mensagem: {ex.Message}");
            throw;
        }
        finally
        {
            // Limpeza
            if (outputFolder != null && Directory.Exists(outputFolder))
            {
                try
                {
                    Directory.Delete(outputFolder, true);
                }
                catch (Exception ex)
                {
                    context.Logger.LogWarning($"Erro ao limpar diretório de saída: {ex.Message}");
                }
            }

            if (localVideoPath != null && File.Exists(localVideoPath))
            {
                try
                {
                    File.Delete(localVideoPath);
                }
                catch (Exception ex)
                {
                    context.Logger.LogWarning($"Erro ao limpar arquivo de vídeo: {ex.Message}");
                }
            }
        }
    }

    private bool IsValidVideoFormat(string fileName)
    {
        var validExtensions = new[] { ".mp4", ".avi", ".mov", ".mkv" };
        return validExtensions.Contains(Path.GetExtension(fileName).ToLower());
    }

    private async Task DownloadVideoFromS3(string key, string localPath)
    {
        var request = new GetObjectRequest
        {
            BucketName = BUCKET_NAME,
            Key = key
        };

        using var response = await _s3Client.GetObjectAsync(request);
        if (response.ResponseStream.Length == 0)
        {
            throw new Exception("Arquivo de vídeo vazio");
        }

        using var fileStream = File.Create(localPath);
        await response.ResponseStream.CopyToAsync(fileStream);
    }

    private async Task UploadThumbnailToS3(string localPath, string key)
    {
        var request = new PutObjectRequest
        {
            BucketName = BUCKET_NAME,
            Key = key,
            FilePath = localPath
        };

        await _s3Client.PutObjectAsync(request);
    }

    private async Task UpdateRedisStatus(string videoId, string status, ILambdaContext context)
    {
        var maxRetries = 3;
        var retryCount = 0;
        var success = false;

        while (retryCount < maxRetries && !success)
        {
            try
            {
                var response = await _httpClient.PostAsync(
                    $"{API_BASE_URL}/status",
                    new StringContent(JsonConvert.SerializeObject(new { videoId, status }), Encoding.UTF8, "application/json"));

                if (response.IsSuccessStatusCode)
                {
                    success = true;
                }
                else
                {
                    retryCount++;
                    if (retryCount < maxRetries)
                    {
                        await Task.Delay(1000 * retryCount); // Backoff exponencial
                    }
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Erro ao atualizar status no Redis: {ex.Message}");
                retryCount++;
                if (retryCount < maxRetries)
                {
                    await Task.Delay(1000 * retryCount);
                }
            }
        }

        if (!success)
        {
            throw new HttpRequestException("Status update failed after multiple retries");
        }
    }

    private async Task UpdateDynamoMetadata(string videoId, int? thumbnailCount, string status, ILambdaContext context)
    {
        try
        {
            var response = await _httpClient.PostAsync(
                $"{API_BASE_URL}/metadata",
                new StringContent(JsonConvert.SerializeObject(new { videoId, thumbnailCount, status }), Encoding.UTF8, "application/json"));

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("Metadata update failed");
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Erro ao atualizar metadados no DynamoDB: {ex.Message}");
            throw;
        }
    }

    private async Task SendNotificationAsync(string videoId, string status, int thumbnailCount, ILambdaContext context)
    {
        try
        {
            var message = new
            {
                videoId,
                status,
                thumbnailCount,
                timestamp = DateTime.UtcNow,
                message = $"Processamento do vídeo {videoId} foi concluído com status {status}. Foram gerados {thumbnailCount} thumbnails."
            };

            var request = new PublishRequest
            {
                TopicArn = SNS_TOPIC_ARN,
                Message = JsonConvert.SerializeObject(message),
                Subject = $"Processamento de Vídeo Concluído - {videoId}"
            };

            await _snsClient.PublishAsync(request);
            context.Logger.LogInformation($"Notificação SNS enviada para o vídeo {videoId}");
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Erro ao enviar notificação SNS: {ex.Message}");
            // Não vamos lançar a exceção aqui para não interromper o fluxo principal
        }
    }
}

public class S3Event
{
    public List<S3EventRecord> Records { get; set; } = new();
}

public class S3EventRecord
{
    public S3Details S3 { get; set; } = new();
}

public class S3Details
{
    public S3Bucket Bucket { get; set; } = new();
    public S3Object Object { get; set; } = new();
}

public class S3Bucket
{
    public string Name { get; set; } = string.Empty;
}

public class S3Object
{
    public string Key { get; set; } = string.Empty;
}
