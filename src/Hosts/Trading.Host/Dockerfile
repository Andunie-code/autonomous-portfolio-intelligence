# Trading.Host image (M5, docs/m5-protocol.md §2–§3): paper trading worker and the Ops API (published on loopback
# only by the compose file). Built and started by the user (scripts/m5-deploy.ps1, m5-paper-start.ps1).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Kernel/ src/Kernel/
COPY src/Infrastructure/ src/Infrastructure/
COPY src/Modules/MarketData/ src/Modules/MarketData/
COPY src/Modules/Execution/ src/Modules/Execution/
COPY src/Modules/Ai/ src/Modules/Ai/
COPY src/Modules/Ai.Gemini/ src/Modules/Ai.Gemini/
COPY src/Modules/Allocator/ src/Modules/Allocator/
COPY src/Hosts/Trading.Host/ src/Hosts/Trading.Host/
RUN dotnet publish src/Hosts/Trading.Host/PortfolioIntelligence.Trading.Host.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "PortfolioIntelligence.Trading.Host.dll"]
