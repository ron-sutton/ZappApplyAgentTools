FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY src/ZappApplyAgentTools/ZappApplyAgentTools.csproj src/ZappApplyAgentTools/
RUN dotnet restore src/ZappApplyAgentTools/ZappApplyAgentTools.csproj
COPY src/ src/
RUN dotnet publish src/ZappApplyAgentTools/ZappApplyAgentTools.csproj -c Release --no-restore -o /out /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:11.0
WORKDIR /app
COPY --from=build /out .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ZappApplyAgentTools.dll"]
