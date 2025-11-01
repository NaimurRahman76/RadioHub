# RadioHub Application Context

## Overview
RadioHub is a web-based radio station application built with ASP.NET Core that allows users to search for songs on YouTube, add them to a queue, and stream them as a radio station.

## Architecture

### Controllers
- **HomeController**: Handles main application pages (Index, Login, Privacy, etc.)
- **SongController**: Manages song search, queue operations, and playback control
- **SignUpController**: Handles user registration
- **CheckController**: Likely for authentication or validation

### Models
- **Song**: Represents a song with YouTube video ID, title, artist, etc.
- **SongSearchResult**: Represents search results from YouTube
- **QueueViewModel**: Contains the current queue and playing song information
- **User**: Represents application users
- **Radio**: Represents radio station information
- **SignUp**: Represents user registration data
- **ErrorViewModel**: Standard error handling model

### Services
- **ISongService/SongService**: Handles YouTube video search and retrieval
- **IQueueService/QueueService**: Manages the song queue and playback state
- **IStreamingService/StreamingService**: Handles audio streaming using FFmpeg

### Data Layer
- **ApplicationDbContext**: Entity Framework Core database context
- **ISignUpRepository/SignUpRepository**: Repository pattern for user data

### SignalR Hub
- **RadioHub**: Real-time communication for queue updates

## Key Features
1. **Song Search**: Search YouTube for songs to add to the queue
2. **Queue Management**: Add, remove, and reorder songs in the queue
3. **Streaming**: Stream audio using FFmpeg
4. **Real-time Updates**: SignalR for live queue updates
5. **User Authentication**: User registration and login

## External Dependencies
- **YoutubeExplode**: For YouTube video search and metadata retrieval
- **FFmpeg**: For audio streaming
- **Entity Framework Core**: For database operations
- **SignalR**: For real-time communication

## File Structure
```
Controllers/
  - HomeController.cs
  - SongController.cs
  - SignUpController.cs
  - CheckController.cs

Models/
  - Song.cs
  - SongSearchResult.cs
  - QueueViewModel.cs
  - User.cs
  - Radio.cs
  - SignUp.cs
  - ErrorViewModel.cs

Services/
  - ISongService.cs
  - SongService.cs
  - IQueueService.cs
  - QueueService.cs
  - IStreamingService.cs
  - StreamingService.cs

Data/
  - ApplicationDbContext.cs

Repository/
  - ISignUpRepository.cs
  - SignUpRepository.cs

Hubs/
  - RadioHub.cs

Views/
  - Home/
  - Song/
  - SignUp/
  - Check/
  - Shared/
```

## API Endpoints

### SongController
- GET /Song - Main song queue page
- GET /Song/Search - Search page view
- POST /Song/Search - Search for songs
- POST /Song/RequestSong - Add a song to the queue
- POST /Song/PlayNextSong - Play the next song in queue
- POST /Song/StopCurrentSong - Stop the currently playing song
- POST /Song/ClearQueue - Clear the entire queue
- POST /Song/RemoveSong - Remove a specific song from queue
- GET /Song/QueueStatus - Get current queue status

## Database
The application uses Entity Framework Core with migrations for database schema management.

## Configuration
- appsettings.json contains application configuration
- The application runs on https://localhost:7288 and http://localhost:5084

## Recent Fixes
1. Fixed namespace issues in Program.cs
2. Resolved nullable TimeSpan issues in QueueViewModel.cs
3. Fixed async method warnings in StreamingService.cs
4. Resolved SongService.cs issues with System.Linq.Async dependency
5. Fixed type mismatches and method calls in SongService.cs

## Known Issues
- Some System packages show warnings about .NET 6.0 compatibility (non-blocking)
