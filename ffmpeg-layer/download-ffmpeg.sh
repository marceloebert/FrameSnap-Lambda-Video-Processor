#!/bin/bash

# Criar diretórios necessários
mkdir -p ffmpeg/bin

# Baixar FFmpeg estático compilado para Amazon Linux 2
curl -O https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz

# Extrair os arquivos
tar xf ffmpeg-release-amd64-static.tar.xz

# Mover os binários necessários
mv ffmpeg-*-static/ffmpeg ffmpeg/bin/
mv ffmpeg-*-static/ffprobe ffmpeg/bin/

# Limpar arquivos temporários
rm -rf ffmpeg-*-static*

# Criar arquivo de configuração para o Lambda
echo '{"ffmpeg_path": "/opt/ffmpeg/bin/ffmpeg", "ffprobe_path": "/opt/ffmpeg/bin/ffprobe"}' > ffmpeg/config.json

# Criar arquivo README
echo "FFmpeg Layer for AWS Lambda
Version: $(date +%Y-%m-%d)
This layer contains FFmpeg binaries compiled for Amazon Linux 2.
Path: /opt/ffmpeg/bin/ffmpeg
FFprobe Path: /opt/ffmpeg/bin/ffprobe" > ffmpeg/README.md

# Criar zip da layer
zip -r ffmpeg-layer.zip ffmpeg/ 

dotnet lambda-test-tool-6.0

taskkill /IM "dotnet.exe" /F 