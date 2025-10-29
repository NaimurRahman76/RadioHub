# RadioHub - Online Radio Streaming Platform

RadioHub is a web application that allows users to discover and listen to radio stations from around the world. Built with ASP.NET Core, it provides a modern, responsive interface for streaming audio content.

## Features

- 🎵 Stream thousands of radio stations worldwide
- 🔍 Browse radio stations by category
- ❤️ Create personal favorite lists
- 📱 Responsive design for all devices
- 🚀 SEO optimized for search engines
- 🌐 Multi-language support ready

## SEO Features

This project is fully optimized for search engines with:

- **Dynamic sitemap.xml** - Automatically generated sitemap for all pages
- **robots.txt** - Proper crawling instructions for search engines
- **Meta tags** - Comprehensive SEO meta tags including Open Graph and Twitter cards
- **Structured data** - JSON-LD structured data for better search visibility
- **Canonical URLs** - Proper canonical URL implementation
- **Page titles and descriptions** - Optimized for each page

## Deployment Options

### Option 1: Deploy to Azure (Recommended)

1. **Create Azure Web App:**
   ```bash
   # Install Azure CLI
   az login
   az webapp create --resource-group your-resource-group --plan your-plan-name --name radiohub-app --runtime "DOTNET|6.0"
   ```

2. **Deploy the application:**
   ```bash
   # Build and publish
   dotnet publish -c Release -o ./publish

   # Deploy to Azure
   az webapp deploy --resource-group your-resource-group --name radiohub-app --src-path ./publish
   ```

3. **Configure database connection:**
   - Update `appsettings.json` with your SQL Server connection string
   - Set up Azure SQL Database or use an existing SQL Server

### Option 2: Deploy to IIS (Windows Server)

1. **Install IIS and .NET 6 Hosting Bundle**
2. **Publish the application:**
   ```bash
   dotnet publish -c Release -o C:\inetpub\wwwroot\RadioHub
   ```
3. **Configure IIS:**
   - Create a new website in IIS Manager
   - Point to the published folder
   - Enable necessary IIS features

### Option 3: Deploy to Linux (Ubuntu/Debian)

1. **Install .NET 6 Runtime:**
   ```bash
   wget https://packages.microsoft.com/config/ubuntu/20.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
   sudo dpkg -i packages-microsoft-prod.deb
   rm packages-microsoft-prod.deb
   sudo apt-get update
   sudo apt-get install -y dotnet-runtime-6.0
   ```

2. **Deploy using systemd:**
   - Copy published files to `/var/www/radiohub`
   - Create a systemd service file
   - Configure reverse proxy with nginx or Apache

## Post-Deployment SEO Setup

After deploying your application, complete these steps for optimal search visibility:

### 1. Verify robots.txt and sitemap.xml
- Visit `https://yourdomain.com/robots.txt`
- Visit `https://yourdomain.com/sitemap.xml`
- Ensure they load correctly

### 2. Submit to Google Search Console
1. Go to [Google Search Console](https://search.google.com/search-console)
2. Add your property (your domain)
3. Submit your sitemap: `https://yourdomain.com/sitemap.xml`
4. Request indexing of your main pages

### 3. Submit to Bing Webmaster Tools
1. Go to [Bing Webmaster Tools](https://www.bing.com/webmasters)
2. Add your site
3. Submit your sitemap

### 4. Social Media Optimization
- Verify your Open Graph tags using Facebook Debugger
- Test Twitter cards using Twitter Card Validator
- Ensure your images meet social media requirements (1200x630px recommended)

## Environment Configuration

Update the following in `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "your-connection-string-here"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

## Monitoring and Analytics

Consider adding:
- Google Analytics for traffic monitoring
- Google Tag Manager for conversion tracking
- Server monitoring tools
- Error logging and alerting

## SEO Best Practices Implemented

✅ **Technical SEO:**
- Fast loading times
- Mobile-friendly responsive design
- Secure HTTPS connection
- Clean URL structure
- Proper heading hierarchy

✅ **On-Page SEO:**
- Unique, descriptive page titles
- Compelling meta descriptions
- Proper use of heading tags
- Alt text for images
- Internal linking structure

✅ **Content SEO:**
- High-quality, relevant content
- Structured data markup
- Social media integration
- Regular content updates

## Support

For support or questions:
- Create an issue in the GitHub repository
- Check the documentation
- Review the code comments

## License

This project is licensed under the MIT License.
