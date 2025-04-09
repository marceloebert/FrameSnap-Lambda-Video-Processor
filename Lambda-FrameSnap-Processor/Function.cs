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

    public Function()
    {
        BUCKET_NAME = Environment.GetEnvironmentVariable("BUCKET_NAME") ?? "framesnap-video-bucket";
        API_BASE_URL = Environment.GetEnvironmentVariable("API_BASE_URL") ?? "http://a7539c5052e5a4306b61042306074bd2-1313948291.us-east-1.elb.amazonaws.com";

        _s3Client = new AmazonS3Client();
        _httpClient = new HttpClient();

        // Configurar o Xabe.FFmpeg para usar o diretório temporário
        FFmpeg.SetExecutablesPath(TEMP_DIR);
    }

    public async Task FunctionHandler(SQSEvent evnt, ILambdaContext context)
    {
        if (!evnt.Records.Any())
        {
            context.Logger.LogInformation("Nenhum registro SQS recebido.");
            return;
        }

        // Baixar FFmpeg antes de processar os vídeos
        context.Logger.LogInformation("Baixando FFmpeg...");
        await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, TEMP_DIR);
        context.Logger.LogInformation("FFmpeg baixado com sucesso!");

        foreach (var message in evnt.Records)
        {
            await ProcessMessageAsync(message, context);
        }
    }

    private async Task ProcessMessageAsync(SQSEvent.SQSMessage message, ILambdaContext context)
    {
        try
        {
            var s3Event = JsonConvert.DeserializeObject<S3Event>(message.Body);
            var s3Record = s3Event.Records.First();

            var videoKey = s3Record.S3.Object.Key;

            // Ignorar arquivos que não são vídeos
            if (videoKey.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                context.Logger.LogInformation($"Ignorando arquivo ZIP: {videoKey}");
                return;
            }

            var videoId = Path.GetFileNameWithoutExtension(videoKey).Split('_')[0];

            context.Logger.LogInformation($"Processando vídeo: {videoKey}");

            // Atualizar status inicial
            await UpdateRedisStatus(videoId, "PROCESSING", context);
            await UpdateDynamoMetadata(videoId, null, "PROCESSING", context);

            // Download do vídeo do S3
            var localVideoPath = Path.Combine(TEMP_DIR, Path.GetFileName(videoKey));
            await DownloadVideoFromS3(videoKey, localVideoPath);

            // Criar diretório para as imagens
            var outputFolder = Path.Combine(TEMP_DIR, "images");
            Directory.CreateDirectory(outputFolder);

            // Processar o vídeo e gerar thumbnails
            var mediaInfo = await FFmpeg.GetMediaInfo(localVideoPath);
            var duration = mediaInfo.Duration;
            var interval = TimeSpan.FromSeconds(20);

            var tasks = new List<Task>();
            for (var currentTime = TimeSpan.Zero; currentTime < duration; currentTime += interval)
            {
                var outputPath = Path.Combine(outputFolder, $"frame_at_{currentTime.TotalSeconds}.jpg");
                var conversion = await FFmpeg.Conversions.FromSnippet.Snapshot(localVideoPath, outputPath, TimeSpan.FromSeconds(currentTime.TotalSeconds));
                tasks.Add(conversion.Start());
                context.Logger.LogInformation($"Thumbnail gerado: {outputPath}");
            }

            await Task.WhenAll(tasks);

            // Criar arquivo ZIP com os thumbnails
            var zipFileName = $"{videoId}_thumbnails.zip";
            var zipPath = Path.Combine(TEMP_DIR, zipFileName);
            ZipFile.CreateFromDirectory(outputFolder, zipPath);

            // Upload do ZIP para o S3 dentro da pasta thumbnails/
            var zipKey = $"thumbnails/{zipFileName}";
            await UploadZipToS3(zipPath, zipKey);

            // Atualizar status final
            await UpdateRedisStatus(videoId, "COMPLETED", context);
            await UpdateDynamoMetadata(videoId, zipKey, "COMPLETED", context);

            // Limpar arquivos temporários
            File.Delete(localVideoPath);
            File.Delete(zipPath);
            Directory.Delete(outputFolder, true);

            context.Logger.LogInformation($"Processamento concluído para o vídeo: {videoKey}");
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Erro ao processar mensagem: {ex.Message}");
            context.Logger.LogError($"StackTrace: {ex.StackTrace}");
            throw;
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
            context.Logger.LogError($"Erro ao atualizar status no Redis para vídeo {videoId}: {ex.Message}");
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
            context.Logger.LogError($"Erro ao atualizar metadados no DynamoDB para vídeo {videoId}: {ex.Message}");
            throw;
        }
    }
}

public class S3Event
{
    public List<S3EventRecord> Records { get; set; }
}

public class S3EventRecord
{
    public S3Details S3 { get; set; }
}

public class S3Details
{
    public S3Bucket Bucket { get; set; }
    public S3Object Object { get; set; }
}

public class S3Bucket
{
    public string Name { get; set; }
}

public class S3Object
{
    public string Key { get; set; }
}