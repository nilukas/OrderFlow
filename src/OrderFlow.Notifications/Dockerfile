# Build context is the repository root:
#   docker build -f src/OrderFlow.Notifications/Dockerfile -t orderflow-notifications .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props .
COPY src/OrderFlow.Contracts/OrderFlow.Contracts.csproj src/OrderFlow.Contracts/
COPY src/OrderFlow.Notifications/OrderFlow.Notifications.csproj src/OrderFlow.Notifications/
RUN dotnet restore src/OrderFlow.Notifications/OrderFlow.Notifications.csproj
COPY src/OrderFlow.Contracts src/OrderFlow.Contracts
COPY src/OrderFlow.Notifications src/OrderFlow.Notifications
RUN dotnet publish src/OrderFlow.Notifications/OrderFlow.Notifications.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "OrderFlow.Notifications.dll"]
