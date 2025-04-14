# FrameSnap - Processador de Vídeos AWS Lambda

O FrameSnap é um serviço serverless que processa vídeos automaticamente, extraindo frames em intervalos regulares e gerando thumbnails. O serviço é construído usando AWS Lambda e integra diversos serviços AWS para fornecer uma solução robusta e escalável.

## 🚀 Tecnologias

- **.NET 8.0**: Framework principal do projeto
- **AWS Lambda**: Ambiente de execução serverless
- **AWS S3**: Armazenamento de vídeos e thumbnails
- **AWS SQS**: Fila de mensagens para processamento assíncrono
- **AWS SNS**: Notificações de eventos
- **FFmpeg**: Processamento de vídeo (via Lambda Layer)
- **Redis**: Cache e controle de status
- **DynamoDB**: Armazenamento de metadados

## 📋 Pré-requisitos

- .NET 8.0 SDK
- AWS CLI configurado
- FFmpeg Layer configurado no Lambda
- Acesso às seguintes permissões AWS:
  - `s3:GetObject`
  - `s3:PutObject`
  - `sqs:ReceiveMessage`
  - `sns:Publish`
  - `dynamodb:PutItem`
  - `dynamodb:GetItem`

## 🔧 Configuração

### Variáveis de Ambiente

```env
BUCKET_NAME=nome-do-bucket-s3
API_BASE_URL=url-da-api
TEMP_DIR=/tmp
FFMPEG_PATH=/opt/ffmpeg
SNS_TOPIC_ARN=arn:aws:sns:region:account:topic
```

### Estrutura do Projeto

```
Lambda-Framesnap-Video-Processor/
├── Lambda-FrameSnap-Processor/        # Projeto principal
│   ├── Function.cs                    # Handler principal
│   └── Lambda-FrameSnap-Processor.csproj
├── Lambda-FrameSnap-Processor.Tests/  # Testes unitários
│   └── FunctionTests.cs
└── ffmpeg-layer/                      # Layer do FFmpeg
```

## 🎯 Funcionalidades

### Processamento de Vídeo
- Extração automática de frames em intervalos regulares
- Geração de thumbnails em diferentes resoluções
- Suporte a múltiplos formatos de vídeo (MP4, AVI, MOV)
- Compressão e otimização de imagens

### Integrações
- **S3**: Armazenamento de vídeos e thumbnails
- **SQS**: Fila de processamento assíncrono
- **SNS**: Notificações de status e conclusão
- **Redis**: Cache e controle de status em tempo real
- **DynamoDB**: Armazenamento de metadados e informações do vídeo

## 📝 Endpoints e Eventos

### SQS Event
```json
{
  "Records": [
    {
      "s3": {
        "bucket": {
          "name": "bucket-name"
        },
        "object": {
          "key": "video-key.mp4"
        }
      }
    }
  ]
}
```

### SNS Notifications
```json
{
  "videoId": "string",
  "status": "PROCESSING|COMPLETED|FAILED",
  "zipKey": "string",
  "timestamp": "ISO8601"
}
```

## 🧪 Testes

### Execução dos Testes
```bash
dotnet test
```

### Cobertura de Testes
- Testes unitários para todos os fluxos principais
- Mocks para serviços externos (S3, SNS, Redis)
- Validação de formatos de vídeo
- Tratamento de erros

## 📦 Deployment

### Lambda Layer (FFmpeg)
1. Criar layer com FFmpeg
2. Configurar path em `/opt/ffmpeg`
3. Anexar layer à função Lambda

### Função Lambda
```bash
dotnet lambda deploy-function
```

## 🔍 Monitoramento

### CloudWatch Logs
- Logs detalhados de processamento
- Métricas de performance
- Rastreamento de erros

### Métricas
- Tempo de processamento
- Tamanho dos arquivos
- Taxa de sucesso/falha

## ⚠️ Limitações

- Tamanho máximo do vídeo: 500MB
- Duração máxima: 2 horas
- Formatos suportados: MP4, AVI, MOV

## 🔐 Segurança

- IAM Roles com permissões mínimas necessárias
- Validação de inputs
- Sanitização de nomes de arquivo