FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY LibraryExport.csproj .
RUN dotnet restore
COPY Function.cs .
RUN dotnet publish -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "LibraryExport.dll"]
