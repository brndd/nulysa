FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /source
COPY Directory.Build.props .
COPY src/Nulysa/ src/Nulysa/
RUN dotnet restore src/Nulysa/Nulysa.csproj --locked-mode
RUN dotnet publish src/Nulysa/Nulysa.csproj --no-restore -c Release -o /release

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14 AS runtime
WORKDIR /app
COPY --from=build /release .
USER root
RUN mkdir /keys && chown app:app /keys
USER app
ENV ASPNETCORE_URLS=http://+:8080 DataProtection__Path=/keys
EXPOSE 8080
ENTRYPOINT ["dotnet", "Nulysa.dll"]
CMD ["web"]
