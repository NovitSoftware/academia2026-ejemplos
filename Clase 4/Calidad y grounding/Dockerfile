FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Calidad.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ENV ASPNETCORE_URLS=http://+:5300 GRABACIONES=/srv/grabaciones CACHE=/srv/datos HOME=/tmp
WORKDIR /srv/app
COPY --from=build /out .
RUN mkdir -p /srv/datos /srv/grabaciones && chmod 777 /srv/datos /srv/grabaciones

EXPOSE 5300
ENTRYPOINT ["dotnet", "MesaYa.Calidad.dll"]
