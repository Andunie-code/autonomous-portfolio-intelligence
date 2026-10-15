# Holdout evaluator image (ADR-0013, T19). Built and started by the user: src/HoldoutEvaluator/evaluator-up.ps1.
# M3: synthetic window data only. The image digest is recorded as an EvaluatorRelease.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Kernel/ src/Kernel/
COPY src/Infrastructure/ src/Infrastructure/
COPY src/HoldoutEvaluator/ src/HoldoutEvaluator/
RUN dotnet publish src/HoldoutEvaluator/PortfolioIntelligence.HoldoutEvaluator.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "PortfolioIntelligence.HoldoutEvaluator.dll"]
