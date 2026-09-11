FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY MediatorBot.slnx ./
COPY MediatorBot.BehaviorScenarios/MediatorBot.BehaviorScenarios.csproj MediatorBot.BehaviorScenarios/
COPY MediatorBot.Console/MediatorBot.Console.csproj MediatorBot.Console/
COPY MediatorBot.Core/MediatorBot.Core.csproj MediatorBot.Core/
COPY MediatorBot.Infrastructure/MediatorBot.Infrastructure.csproj MediatorBot.Infrastructure/
COPY MediatorBot.Telegram/MediatorBot.Telegram.csproj MediatorBot.Telegram/
COPY MediatorBot.Tests/MediatorBot.Tests.csproj MediatorBot.Tests/
RUN dotnet restore MediatorBot.slnx

COPY . .
RUN dotnet test MediatorBot.Tests/MediatorBot.Tests.csproj \
    --configuration Release \
    --no-restore
RUN dotnet publish MediatorBot.Telegram/MediatorBot.Telegram.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

ENV DOTNET_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    Storage__DatabasePath=/app/data/mediator-bot.db

COPY --from=build --chown=$APP_UID:$APP_UID /app/publish ./
RUN mkdir /app/data && chown $APP_UID:$APP_UID /app/data

USER $APP_UID
ENTRYPOINT ["dotnet", "MediatorBot.Telegram.dll"]
