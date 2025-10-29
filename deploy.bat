@echo off
REM RadioHub Windows Deployment Script
REM This script helps deploy RadioHub on Windows systems

echo 🚀 RadioHub Windows Deployment Script
echo ====================================

:menu
echo Please choose a deployment option:
echo 1) Build and Publish for Production
echo 2) Create IIS Deployment Package
echo 3) Setup Development Environment
echo 4) Run SEO Tests
echo 5) Exit
echo.

set /p choice="Enter your choice (1-5): "

if %choice%==1 goto build_publish
if %choice%==2 goto iis_package
if %choice%==3 goto dev_setup
if %choice%==4 goto seo_tests
if %choice%==5 goto exit_script

echo ❌ Invalid option. Please choose 1-5.
goto menu

:build_publish
echo 📦 Building and publishing RadioHub...
echo Cleaning previous builds...
dotnet clean

echo Restoring packages...
dotnet restore

echo Building in Release mode...
dotnet build -c Release

echo Publishing application...
dotnet publish -c Release -o ./publish --self-contained false

echo ✅ Build completed successfully!
echo 📁 Published files are in: ./publish/
goto menu

:iis_package
echo 🪟 Creating IIS Deployment Package...
call :build_publish

echo Creating deployment package...
powershell "Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::CreateFromDirectory('./publish', './RadioHub-IIS-Package.zip')"

echo ✅ IIS package created: RadioHub-IIS-Package.zip
echo 📋 Deployment instructions:
echo 1. Copy the zip file to your IIS server
echo 2. Extract to C:\inetpub\wwwroot\RadioHub
echo 3. Create application pool in IIS Manager
echo 4. Set .NET CLR version to 'No Managed Code'
echo 5. Configure site bindings
goto menu

:dev_setup
echo 💻 Setting up development environment...

echo Installing development certificate...
dotnet dev-certs https --trust

echo ✅ Development environment ready!
echo 🚀 Run the application with: dotnet run
echo 🌐 Access at: https://localhost:5001 or http://localhost:5000
goto menu

:seo_tests
echo 🔍 Running SEO Tests...

echo Testing if application is running...
dotnet run --project . >nul 2>&1 &
set app_pid=%errorlevel%

timeout /t 5 /nobreak >nul

echo Testing robots.txt...
curl -s http://localhost:5000/robots.txt

echo.
echo Testing sitemap.xml...
curl -s http://localhost:5000/sitemap.xml

if not %app_pid%==0 (
    taskkill /pid %app_pid% /f >nul 2>&1
)

echo.
echo ✅ SEO tests completed!
goto menu

:exit_script
echo 👋 Goodbye!
pause
exit /b 0
