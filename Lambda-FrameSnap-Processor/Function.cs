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
        API_BASE_URL = Environment.GetEnvironmentVariable("API_BASE_URL") ?? "http://a7ab35083428b4f2389d506576ae8224-1198331366.us-east-1.elb.amazonaws.com";
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
        string videoId = null;
        int thumbnailCount = 0;
        string tempVideoPath = null;
        string tempDir = null;

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

            videoId = Path.GetFileNameWithoutExtension(videoKey).Split('_')[0];
            if (string.IsNullOrEmpty(videoId))
            {
                context.Logger.LogWarning("ID do vídeo inválido");
                return;
            }

            context.Logger.LogInformation($"Processando vídeo: {videoKey}");

            await UpdateRedisStatus(videoId, "PROCESSING", context);
            await UpdateDynamoMetadata(videoId, null, "PROCESSING", context);

            tempVideoPath = Path.Combine(TEMP_DIR, Path.GetFileName(videoKey));
            await DownloadVideoFromS3(videoKey, tempVideoPath);

            tempDir = Path.Combine(TEMP_DIR, "images");
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
                Directory.CreateDirectory(tempDir);
            }
            catch (IOException ex)
            {
                throw new IOException($"Não foi possível criar o diretório de saída: {ex.Message}");
            }

            var mediaInfo = await _videoProcessor.GetMediaInfo(tempVideoPath);
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
            thumbnailCount = (int)(duration.TotalSeconds / interval.TotalSeconds);

            for (int i = 0; i < thumbnailCount; i++)
            {
                var timestamp = i * interval;
                var outputPath = Path.Combine(tempDir, $"thumbnail_{i}.jpg");

                await _videoProcessor.GenerateThumbnail(tempVideoPath, outputPath, timestamp);

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
            if (videoId != null)
            {
                try
                {
                    await UpdateRedisStatus(videoId, "ERROR", context);
                    await UpdateDynamoMetadata(videoId, thumbnailCount, "ERROR", context);
                    await SendNotificationAsync(videoId, "ERROR", thumbnailCount, context);
                }
                catch (Exception updateEx)
                {
                    context.Logger.LogError($"Erro ao atualizar status de erro: {updateEx.Message}");
                }
            }
            throw;
        }
        finally
        {
            // Limpeza
            if (tempDir != null && Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch (Exception ex)
                {
                    context.Logger.LogWarning($"Erro ao limpar diretório de saída: {ex.Message}");
                }
            }

            if (tempVideoPath != null && File.Exists(tempVideoPath))
            {
                try
                {
                    File.Delete(tempVideoPath);
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
        const int maxRetries = 3;
        int retryCount = 0;
        bool success = false;

        while (!success && retryCount < maxRetries)
        {
            try
            {
                context.Logger.LogInformation($"Tentativa {retryCount + 1} de atualizar status no Redis para o vídeo {videoId}");
                
                var response = await _httpClient.PostAsync(
                    $"{API_BASE_URL}/status",
                    new StringContent(JsonConvert.SerializeObject(new { videoId, status }), Encoding.UTF8, "application/json"));

                if (response.IsSuccessStatusCode)
                {
                    success = true;
                    context.Logger.LogInformation($"Status atualizado com sucesso para o vídeo {videoId}");
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    context.Logger.LogError($"Erro ao atualizar status. Status code: {response.StatusCode}, Conteúdo: {errorContent}");
                    retryCount++;
                    if (retryCount < maxRetries)
                    {
                        var delay = 1000 * (int)Math.Pow(2, retryCount); // Backoff exponencial
                        context.Logger.LogInformation($"Aguardando {delay}ms antes da próxima tentativa");
                        await Task.Delay(delay);
                    }
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Erro ao atualizar status no Redis: {ex.Message}");
                context.Logger.LogError($"Stack trace: {ex.StackTrace}");
                retryCount++;
                if (retryCount < maxRetries)
                {
                    var delay = 1000 * (int)Math.Pow(2, retryCount);
                    context.Logger.LogInformation($"Aguardando {delay}ms antes da próxima tentativa");
                    await Task.Delay(delay);
                }
            }
        }

        if (!success)
        {
            context.Logger.LogError($"Falha ao atualizar status após {maxRetries} tentativas para o vídeo {videoId}");
            // Em vez de lançar exceção, vamos apenas logar o erro e continuar
            // Isso evita que o processamento do vídeo seja interrompido por problemas de comunicação com o Redis
            return;
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
