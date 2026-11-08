FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY src/ZappApplyAgentTools/ZappApplyAgentTools.csproj src/ZappApplyAgentTools/
RUN dotnet restore src/ZappApplyAgentTools/ZappApplyAgentTools.csproj
COPY src/ src/
RUN dotnet publish src/ZappApplyAgentTools/ZappApplyAgentTools.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM build AS verify
COPY tests/ZappApplyAgentTools.Checks/ tests/ZappApplyAgentTools.Checks/
RUN dotnet run --project tests/ZappApplyAgentTools.Checks/ZappApplyAgentTools.Checks.csproj -c Release -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=verify /out .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=5 \
    CMD curl --fail --silent --show-error --max-time 4 http://127.0.0.1:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "ZappApplyAgentTools.dll"]
