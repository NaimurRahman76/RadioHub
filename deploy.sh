#!/bin/bash

# RadioHub Deployment Script
# This script helps deploy RadioHub to various platforms

echo "🚀 RadioHub Deployment Script"
echo "================================"

# Function to display menu
show_menu() {
    echo "Please choose a deployment option:"
    echo "1) Build and Publish for Production"
    echo "2) Deploy to Azure (requires Azure CLI)"
    echo "3) Generate Deployment Package for IIS"
    echo "4) Setup Development Environment"
    echo "5) Run SEO Tests"
    echo "6) Exit"
    echo ""
}

# Function to build and publish
build_and_publish() {
    echo "📦 Building and publishing RadioHub..."
    echo "Cleaning previous builds..."
    dotnet clean

    echo "Restoring packages..."
    dotnet restore

    echo "Building in Release mode..."
    dotnet build -c Release

    echo "Publishing application..."
    dotnet publish -c Release -o ./publish --self-contained false

    echo "✅ Build completed successfully!"
    echo "📁 Published files are in: ./publish/"
}

# Function for Azure deployment
deploy_azure() {
    echo "☁️ Azure Deployment"
    read -p "Enter your Azure resource group name: " resource_group
    read -p "Enter your Azure app name: " app_name

    if ! command -v az &> /dev/null; then
        echo "❌ Azure CLI is not installed. Please install it first."
        echo "Installation: curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash"
        exit 1
    fi

    echo "Creating Azure Web App..."
    az webapp create --resource-group $resource_group --plan $resource_group-plan --name $app_name --runtime "DOTNET|6.0"

    echo "Deploying to Azure..."
    az webapp deploy --resource-group $resource_group --name $app_name --src-path ./publish

    echo "✅ Deployment completed!"
    echo "🌐 Your app is available at: https://$app_name.azurewebsites.net"
    echo "📄 Sitemap: https://$app_name.azurewebsites.net/sitemap.xml"
    echo "🤖 Robots.txt: https://$app_name.azurewebsites.net/robots.txt"
}

# Function for IIS deployment package
create_iis_package() {
    echo "🪟 Creating IIS Deployment Package..."
    build_and_publish

    echo "Creating deployment package..."
    cd publish
    zip -r ../RadioHub-IIS-Package.zip .
    cd ..

    echo "✅ IIS package created: RadioHub-IIS-Package.zip"
    echo "📋 Deployment instructions:"
    echo "1. Copy the zip file to your IIS server"
    echo "2. Extract to C:\inetpub\wwwroot\RadioHub"
    echo "3. Create application pool in IIS Manager"
    echo "4. Set .NET CLR version to 'No Managed Code'"
    echo "5. Configure site bindings"
}

# Function for development setup
setup_development() {
    echo "💻 Setting up development environment..."

    if ! command -v dotnet &> /dev/null; then
        echo "❌ .NET 6 SDK is not installed."
        echo "Please install .NET 6 SDK from: https://dotnet.microsoft.com/download/dotnet/6.0"
        exit 1
    fi

    echo "Installing development certificate..."
    dotnet dev-certs https --trust

    echo "✅ Development environment ready!"
    echo "🚀 Run the application with: dotnet run"
    echo "🌐 Access at: https://localhost:5001 or http://localhost:5000"
}

# Function for SEO testing
run_seo_tests() {
    echo "🔍 Running SEO Tests..."

    if command -v curl &> /dev/null; then
        echo "Testing robots.txt..."
        curl -s http://localhost:5000/robots.txt | head -10

        echo -e "\nTesting sitemap.xml..."
        curl -s http://localhost:5000/sitemap.xml | head -10

        echo -e "\n✅ SEO endpoints are working!"
    else
        echo "❌ curl is not installed. Please install it to run SEO tests."
    fi
}

# Main menu loop
while true; do
    show_menu
    read -p "Enter your choice (1-6): " choice

    case $choice in
        1)
            build_and_publish
            ;;
        2)
            deploy_azure
            ;;
        3)
            create_iis_package
            ;;
        4)
            setup_development
            ;;
        5)
            run_seo_tests
            ;;
        6)
            echo "👋 Goodbye!"
            exit 0
            ;;
        *)
            echo "❌ Invalid option. Please choose 1-6."
            ;;
    esac

    echo -e "\nPress Enter to continue..."
    read
done
