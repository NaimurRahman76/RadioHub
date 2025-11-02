# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

RadioHub is an ASP.NET Core 6.0 web application for streaming radio stations and managing song requests. It combines traditional radio station discovery with a modern YouTube-based song request system using SignalR for real-time updates.

## Core Architecture

### Technology Stack
- **.NET 6.0** with Entity Framework Core 7.0.5
- **SQL Server** database with Code First migrations
- **SignalR** for real-time song queue updates
- **YouTube API** integration (Google.Apis.YouTube.v3, YoutubeExplode)
- **ASP.NET MVC** pattern with Razor views

### Key Components

#### SignalR Integration
- **Hub Location**: `Hubs/RadioHub.cs` mapped to `/radiohub`
- **Real-time Features**: Song queue updates, now playing notifications
- **Service Registration**: Singleton `IStreamingService` for managing state across requests

#### Song Request System
- **YouTube Integration**: Uses YouTube API for song search and metadata
- **Queue Management**: `Services/StreamingService.cs` manages song playback queue
- **Search Controller**: `Controllers/SongController.cs` handles YouTube search and song requests

#### Database Context
- **Context**: `Data/ApplicationDbContext.cs`
- **Entities**: User, Radio, Song with many-to-many relationship for user favorites
- **Connection String**: Configure in `appsettings.json` under "DefaultConnection"

### Service Layer Architecture
- **ISongService/SongService**: YouTube API operations (search, get details)
- **IStreamingService/StreamingService**: Queue management and playback state
- **ISignUpRepository/SignUpRepository**: User registration data access

## Development Commands

### Build and Run
```bash
# Build the project
dotnet build

# Run in development mode
dotnet run

# Publish for deployment
dotnet publish -c Release -o ./publish
```

### Database Operations
```bash
# Add new migration
dotnet ef migrations add MigrationName

# Update database
dotnet ef database update

# Note: EF Tools package included for migrations
```

### Package Management
```bash
# Restore packages
dotnet restore

# List packages (notable ones listed in .csproj)
# - Entity Framework Core 7.0.5
# - Google.Apis.YouTube.v3 1.62.0.3169
# - YoutubeExplode 6.5.6
```

## Project Structure

### Controllers
- `HomeController.cs`: Main pages, SEO routes (robots.txt, sitemap.xml)
- `SongController.cs`: YouTube search, song requests, queue status API
- `SignUpController.cs`: User registration
- `CheckController.cs`: Admin/validation functionality

### Models
- `Song.cs`: Core entity with YouTube integration, queue timing logic
- `Radio.cs`: Radio station entity
- `User.cs`: User entity with favorite radios relationship
- `QueueViewModel.cs`, `SongSearchResult.cs`: View-specific models

### Services
- **Singleton Scope**: `StreamingService` maintains application-wide song queue state
- **Scoped Services**: `SongService` for YouTube operations per request

## Configuration Notes

### Session Management
- Session configured with 30-minute timeout
- Cookie name: "UserIdSession"
- Uses distributed memory cache

### SEO Features
- Dynamic sitemap.xml generation via `HomeController/SitemapXml`
- robots.txt via `HomeController/RobotsTxt`
- Structured data and meta tags implemented

### Encoding Support
- Legacy encoding support enabled: `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`
- Important for YouTube metadata processing

## Development Patterns

### Error Handling
- Controllers use try-catch blocks with JSON error responses
- Logging integrated throughout the application
- User-friendly error messages for API endpoints

### Real-time Updates
- All song queue changes broadcast via SignalR
- Frontend connects to `/radiohub` endpoint
- Queue updates triggered on song enqueue operations

### YouTube Integration
- Video ID extraction and validation
- Metadata retrieval (title, channel, duration, thumbnails)
- Duration handling with fallback to 12 seconds