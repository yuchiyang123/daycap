FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY DayCap.Api/DayCap.Api.csproj DayCap.Api/
RUN dotnet restore "DayCap.Api/DayCap.Api.csproj"
COPY DayCap.Api/. DayCap.Api/
WORKDIR /src/DayCap.Api
RUN dotnet publish "DayCap.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
# curl 給 healthcheck 用；tzdata 讓 Asia/Taipei 時區可用（沒有也會退回固定 +8）。
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app/data
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "DayCap.Api.dll"]
