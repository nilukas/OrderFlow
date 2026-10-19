# Build context is the repository root:
#   docker build -f src/OrderFlow.Orders/Dockerfile -t orderflow-orders .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props .
COPY src/OrderFlow.Contracts/OrderFlow.Contracts.csproj src/OrderFlow.Contracts/
COPY src/OrderFlow.Orders/OrderFlow.Orders.csproj src/OrderFlow.Orders/
RUN dotnet restore src/OrderFlow.Orders/OrderFlow.Orders.csproj
COPY src/OrderFlow.Contracts src/OrderFlow.Contracts
COPY src/OrderFlow.Orders src/OrderFlow.Orders
RUN dotnet publish src/OrderFlow.Orders/OrderFlow.Orders.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "OrderFlow.Orders.dll"]
