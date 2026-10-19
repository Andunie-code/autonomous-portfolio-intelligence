# Holdout evaluator image (ADR-0013, T19; docs/m5-protocol.md §6, §10 E9). Built by the user (scripts/m5-deploy.ps1);
# the image digest is recorded as an EvaluatorRelease. The same image carries the H-ENV probe (/app/probe), so the
# probe runs from the evaluator's own network position. The build context excludes secrets (.dockerignore).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Kernel/ src/Kernel/
COPY src/Infrastructure/ src/Infrastructure/
COPY src/HoldoutEvaluator/ src/HoldoutEvaluator/
RUN dotnet publish src/HoldoutEvaluator/PortfolioIntelligence.HoldoutEvaluator.csproj -c Release -o /app --no-self-contained \
 && dotnet publish src/HoldoutEvaluator/EnvironmentProbe/PortfolioIntelligence.HoldoutEvaluator.EnvironmentProbe.csproj -c Release -o /app/probe --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "PortfolioIntelligence.HoldoutEvaluator.dll"]
