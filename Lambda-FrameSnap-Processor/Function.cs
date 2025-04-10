using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.S3;
using Amazon.S3.Model;
using Newtonsoft.Json;
using System.Drawing;
using System.IO.Compression;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Lambda_FrameSnap_Processor;

public class Function
{
    private readonly IAmazonS3 _s3Client;
    private readonly HttpClient _httpClient;
    private readonly string BUCKET_NAME;
    private readonly string API_BASE_URL;
    private const string TEMP_DIR = "/tmp";
    private const string FFMPEG_PATH = "/opt/ffmpeg/ffmpeg";
    private const string FFPROBE_PATH = "/opt/ffmpeg/ffprobe";

    // Construtor padrão para produção
    public Function()
        : this(new AmazonS3Client(), new HttpClient()) { }

    // Construtor com injeção de dependência para testes
    public Function(IAmazonS3 s3Client, HttpClient httpClient)
    {
        BUCKET_NAME = Environment.GetEnvironmentVariable("BUCKET_NAME") ?? "framesnap-video-bucket";
        API_BASE_URL = Environment.GetEnvironmentVariable("API_BASE_URL") ?? "http://a7ab35083428b4f2389d506576ae8224-1198331366.us-east-1.elb.amazonaws.com";

        _s3Client = s3Client;
        _httpClient = httpClient;

        // Configurar caminhos do FFmpeg
        FFmpeg.SetExecutablesPath("/opt/ffmpeg");
    }

    public async Task FunctionHandler(SQSEvent evnt, ILambdaContext context)
    {
        if (!evnt.Records.Any())
        {
            context.Logger.LogWarning("Nenhuma mensagem para processar.");
            return;
        }

        try
        {
            // Verificar se o FFmpeg está disponível
            context.Logger.LogInformation($"Verificando FFmpeg em {FFMPEG_PATH}");
            if (!File.Exists(FFMPEG_PATH))
            {
                context.Logger.LogError($"FFmpeg não encontrado em {FFMPEG_PATH}");
                throw new Exception($"FFmpeg não encontrado em {FFMPEG_PATH}");
            }

            context.Logger.LogInformation($"Verificando FFprobe em {FFPROBE_PATH}");
            if (!File.Exists(FFPROBE_PATH))
            {
                context.Logger.LogError($"FFprobe não encontrado em {FFPROBE_PATH}");
                throw new Exception($"FFprobe não encontrado em {FFPROBE_PATH}");
            }

            context.Logger.LogInformation("FFmpeg e FFprobe encontrados com sucesso!");

            foreach (var message in evnt.Records)
            {
                await ProcessMessageAsync(message, context);
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Erro ao processar mensagens: {ex.Message}");
            context.Logger.LogError($"Stack trace: {ex.StackTrace}");
            throw;
        }
    }

    private async Task ProcessMessageAsync(SQSEvent.SQSMessage message, ILambdaContext context)
    {
        string videoId = null;
        string localVideoPath = null;
        string outputFolder = null;
        string zipPath = null;

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

            videoId = Path.GetFileNameWithoutExtension(videoKey).Split('_')[0];
            context.Logger.LogInformation($"Processando vídeo: {videoKey} (ID: {videoId})");

            try
            {
                await UpdateRedisStatus(videoId, "PROCESSING", context);
                await UpdateDynamoMetadata(videoId, null, "PROCESSING", context);
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning($"Erro ao atualizar status inicial, mas continuando processamento: {ex.Message}");
            }

            localVideoPath = Path.Combine(TEMP_DIR, Path.GetFileName(videoKey));
            await DownloadVideoFromS3(videoKey, localVideoPath);
            context.Logger.LogInformation($"Vídeo baixado para {localVideoPath}");

            outputFolder = Path.Combine(TEMP_DIR, "images");
            if (Directory.Exists(outputFolder))
            {
                Directory.Delete(outputFolder, true);
            }
            Directory.CreateDirectory(outputFolder);
            context.Logger.LogInformation($"Diretório de saída criado: {outputFolder}");

            context.Logger.LogInformation("Obtendo informações do vídeo...");
            var mediaInfo = await FFmpeg.GetMediaInfo(localVideoPath);
            if (mediaInfo == null)
            {
                throw new Exception("Não foi possível obter informações do vídeo");
            }

            var duration = mediaInfo.Duration;
            context.Logger.LogInformation($"Duração do vídeo: {duration.TotalSeconds} segundos");

            var interval = TimeSpan.FromSeconds(20);
            var thumbnailCount = (int)(duration.TotalSeconds / interval.TotalSeconds);
            context.Logger.LogInformation($"Gerando {thumbnailCount} thumbnails...");

            var tasks = new List<Task>();
            for (var currentTime = TimeSpan.Zero; currentTime < duration; currentTime += interval)
            {
                var outputPath = Path.Combine(outputFolder, $"frame_at_{currentTime.TotalSeconds}.jpg");
                var conversion = await FFmpeg.Conversions.FromSnippet.Snapshot(localVideoPath, outputPath, currentTime);
                tasks.Add(conversion.Start());
            }

            await Task.WhenAll(tasks);
            context.Logger.LogInformation($"Gerados {thumbnailCount} thumbnails com sucesso");

            var zipFileName = $"{videoId}_thumbnails.zip";
            zipPath = Path.Combine(TEMP_DIR, zipFileName);
            ZipFile.CreateFromDirectory(outputFolder, zipPath);
            context.Logger.LogInformation($"Arquivo ZIP criado: {zipPath}");

            var zipKey = $"thumbnails/{zipFileName}";
            await UploadZipToS3(zipPath, zipKey);
            context.Logger.LogInformation($"ZIP enviado para S3: {zipKey}");

            try
            {
                await UpdateRedisStatus(videoId, "COMPLETED", context);
                await UpdateDynamoMetadata(videoId, zipKey, "COMPLETED", context);
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning($"Erro ao atualizar status final, mas processamento foi concluído: {ex.Message}");
            }

            context.Logger.LogInformation($"Processamento concluído para o vídeo: {videoKey}");
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Erro ao processar mensagem: {ex.Message}");
            context.Logger.LogError($"Stack trace: {ex.StackTrace}");

            if (videoId != null)
            {
                try
                {
                    await UpdateRedisStatus(videoId, "ERROR", context);
                    await UpdateDynamoMetadata(videoId, null, "ERROR", context);
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
            try
            {
                // Limpeza dos arquivos temporários
                if (localVideoPath != null && File.Exists(localVideoPath))
                {
                    File.Delete(localVideoPath);
                }
                if (zipPath != null && File.Exists(zipPath))
                {
                    File.Delete(zipPath);
                }
                if (outputFolder != null && Directory.Exists(outputFolder))
                {
                    Directory.Delete(outputFolder, true);
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning($"Erro durante a limpeza dos arquivos temporários: {ex.Message}");
            }
        }
    }

    private async Task DownloadVideoFromS3(string key, string localPath)
    {
        var response = await _s3Client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = BUCKET_NAME,
            Key = key
        });

        using var fileStream = File.Create(localPath);
        await response.ResponseStream.CopyToAsync(fileStream);
    }

    private async Task UploadZipToS3(string localPath, string key)
    {
        await _s3Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = BUCKET_NAME,
            Key = key,
            FilePath = localPath
        });
    }

    private async Task UpdateRedisStatus(string videoId, string status, ILambdaContext context)
    {
        try
        {
            context.Logger.LogInformation($"Atualizando status no Redis para vídeo {videoId}: {status}");
            var url = $"{API_BASE_URL}/videos/{videoId}/status";

            var content = new StringContent(
                JsonConvert.SerializeObject(new { status }),
                System.Text.Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.PutAsync(url, content);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Erro ao atualizar status no Redis: {ex.Message}");
            throw;
        }
    }

    private async Task UpdateDynamoMetadata(string videoId, string? zipKey, string status, ILambdaContext context)
    {
        try
        {
            context.Logger.LogInformation($"Atualizando metadados no DynamoDB para vídeo {videoId}");
            var url = $"{API_BASE_URL}/videos/{videoId}";

            var metadata = new
            {
                thumbnailFileName = zipKey,
                thumbnailUrl = zipKey != null ? $"https://{BUCKET_NAME}.s3.amazonaws.com/{zipKey}" : null,
                status = status
            };

            var content = new StringContent(
                JsonConvert.SerializeObject(metadata),
                System.Text.Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.PutAsync(url, content);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Erro ao atualizar metadados no DynamoDB: {ex.Message}");
            throw;
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
