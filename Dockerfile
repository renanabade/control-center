# A API no Linux, com aparelhos simulados: um container não alcança o pipe nem o hardware do Windows.
# Serve para experimentar a API sem instalar nada. Para controlar os aparelhos de verdade, use o serviço do Windows.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
# os .csproj primeiro: mudar só o código não baixa os pacotes de novo
COPY src/ControlCenter.Contracts/ControlCenter.Contracts.csproj src/ControlCenter.Contracts/
COPY src/ControlCenter.Api/ControlCenter.Api.csproj src/ControlCenter.Api/
RUN dotnet restore src/ControlCenter.Api/ControlCenter.Api.csproj
COPY src/ControlCenter.Contracts/ src/ControlCenter.Contracts/
COPY src/ControlCenter.Api/ src/ControlCenter.Api/
RUN dotnet publish src/ControlCenter.Api/ControlCenter.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir /data && chown app:app /data
ENV Urls=http://0.0.0.0:8080 \
    Ponte__Modo=Simulada \
    Dados__Pasta=/data
USER app
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "ControlCenter.Api.dll"]
