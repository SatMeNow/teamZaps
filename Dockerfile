# Base image for runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app

# SDK image for building
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY ["src/teamZaps.csproj", "src/"]
RUN dotnet restore "src/teamZaps.csproj"

# Copy the rest of the source code
COPY . .
WORKDIR "/src/src"

# Build the application
ARG VERSION=0.0.0.0-local
RUN dotnet build "teamZaps.csproj" -c Release -o /app/build /p:Version=${VERSION}

# Publish the application
FROM build AS publish
ARG VERSION=0.0.0.0-local
RUN dotnet publish "teamZaps.csproj" -c Release -o /app/publish /p:UseAppHost=false /p:Version=${VERSION}

# Final stage/image
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Create directory for data/logs if needed (optional, based on app needs)
# RUN mkdir -p /app/data

ENTRYPOINT ["dotnet", "teamZaps.dll"]
