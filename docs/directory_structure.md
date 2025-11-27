# SoftMedia Directory Structure

## Root
```
/SoftMedia
├── /docs                   # Documentation (SDD, Checklist, Rules)
├── /src
│   ├── /SoftMedia.Server   # ASP.NET Core Web API (Backend)
│   └── /SoftMedia.Client   # React + Vite (Frontend)
├── /media                  # Default media folder (for testing)
├── /data                   # Database and config storage (runtime)
├── .gitignore
├── SoftMedia.sln           # .NET Solution file
└── README.md
```

## Backend Structure (`/src/SoftMedia.Server`)
```
/SoftMedia.Server
├── /Controllers            # API Endpoints (REST)
├── /Models                 # Database Entities (EF Core)
├── /DTOs                   # Data Transfer Objects (API Contracts)
├── /Services               # Business Logic (Metadata, Auth, FFmpeg)
├── /Data                   # EF Core Context & Migrations
├── /Helpers                # Utilities (Hashing, FileSystem)
├── /Properties             # Launch Settings
├── appsettings.json        # Configuration
└── Program.cs              # Entry Point & DI Setup
```

## Frontend Structure (`/src/SoftMedia.Client`)
```
/SoftMedia.Client
├── /public                 # Static Assets (Favicon, Manifest)
├── /src
│   ├── /assets             # Images, Fonts
│   ├── /components         # Reusable UI Components (Buttons, Cards)
│   ├── /features           # Feature-specific Logic (Auth, Library, Player)
│   ├── /hooks              # Custom React Hooks
│   ├── /pages              # Route Pages (Home, Login, Settings)
│   ├── /services           # API Client (Axios/Fetch)
│   ├── /store              # Global State (Zustand)
│   ├── /types              # TypeScript Interfaces
│   ├── App.tsx             # Main App Component
│   └── main.tsx            # Entry Point
├── index.html
├── package.json
├── tailwind.config.js
├── tsconfig.json
└── vite.config.ts
```
