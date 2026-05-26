FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/TradeDemo.Api/TradeDemo.Api.csproj ./TradeDemo.Api/
RUN dotnet restore TradeDemo.Api/TradeDemo.Api.csproj
COPY src/TradeDemo.Api/ ./TradeDemo.Api/
WORKDIR /src/TradeDemo.Api
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TradeDemo.Api.dll"]
