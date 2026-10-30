# Research.Host image (M5, ADR-0013 §1): job based; `job calibrate-quick` is the heavy synthetic load of the cycle-time
# test and `job henv-credentials` the H25 self-check. Never contains Execution code or exchange credentials.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Kernel/ src/Kernel/
COPY src/Infrastructure/ src/Infrastructure/
COPY src/Modules/MarketData/ src/Modules/MarketData/
COPY src/Modules/Ai/ src/Modules/Ai/
COPY src/Modules/Researcher/ src/Modules/Researcher/
COPY src/Modules/Research/ src/Modules/Research/
COPY src/Modules/SourceGateway/ src/Modules/SourceGateway/
COPY src/Hosts/Research.Host/ src/Hosts/Research.Host/
RUN dotnet publish src/Hosts/Research.Host/PortfolioIntelligence.Research.Host.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "PortfolioIntelligence.Research.Host.dll"]
