FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json .
COPY Gamertab/Gamertab.csproj Gamertab/
COPY functions/LibraryExport/LibraryExport.csproj functions/LibraryExport/
RUN dotnet restore Gamertab/Gamertab.csproj
COPY Gamertab/ Gamertab/
COPY functions/LibraryExport/ functions/LibraryExport/
RUN dotnet publish Gamertab/Gamertab.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Gamertab.dll"]
