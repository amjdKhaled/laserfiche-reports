ARG OLLAMA_IMAGE=ollama/ollama:latest
FROM ${OLLAMA_IMAGE} AS ollama
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/ src/
RUN dotnet publish src/LaserficheReports.Web/LaserficheReports.Web.csproj -c Release -o /out /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble
USER root
RUN apt-get update && apt-get install -y --no-install-recommends python3 python3-venv ca-certificates libvulkan1 libopenblas0 \
    && rm -rf /var/lib/apt/lists/*
COPY deployment/docker/certs/ /usr/local/share/ca-certificates/
RUN update-ca-certificates
COPY --from=ollama /usr/bin/ollama /usr/bin/ollama
COPY --from=ollama /usr/lib/ollama /usr/lib/ollama
WORKDIR /app
COPY tools/reports-graph/requirements.txt /app/graph/requirements.txt
RUN python3 -m venv /opt/graph && /opt/graph/bin/pip install --no-cache-dir -r /app/graph/requirements.txt "Pillow>=12.3,<13"
COPY --from=build /out /app/web
COPY tools/reports-graph/*.py /app/graph/
COPY deployment/supervise.py /app/supervise.py
COPY deployment/docker/convert_image.py /app/convert_image.py
RUN mkdir -p /app/logs /app/web/config /models /home/app/.aspnet/DataProtection-Keys \
    && chown -R app:app /app/logs /app/web/config /models /home/app
ENV OLLAMA_MODELS=/models HOME=/home/app ASPNETCORE_ENVIRONMENT=Production \
    REPORTS_IMAGE_PYTHON=/opt/graph/bin/python REPORTS_IMAGE_CONVERTER=/app/convert_image.py
USER app
EXPOSE 5187
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s CMD ["/opt/graph/bin/python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:5187/', timeout=3)"]
ENTRYPOINT ["/opt/graph/bin/python", "/app/supervise.py", "--root", "/app", "--bind", "0.0.0.0"]
