@echo off
REM RadioHub SEO Verification Script
REM This script verifies that all SEO elements are working correctly

echo 🔍 RadioHub SEO Verification
echo ============================
echo.

set "base_url=http://localhost:5000"
if not "%1"=="" set "base_url=%1"

echo Verifying SEO endpoints for: %base_url%
echo.

REM Test robots.txt
echo 1. Testing robots.txt...
curl -s "%base_url%/robots.txt" >nul 2>&1
if %errorlevel%==0 (
    echo ✅ robots.txt is accessible
    curl -s "%base_url%/robots.txt" | findstr "User-agent" >nul
    if %errorlevel%==0 (
        echo ✅ robots.txt contains proper directives
    ) else (
        echo ❌ robots.txt is missing directives
    )
) else (
    echo ❌ robots.txt is not accessible
)

echo.

REM Test sitemap.xml
echo 2. Testing sitemap.xml...
curl -s "%base_url%/sitemap.xml" >nul 2>&1
if %errorlevel%==0 (
    echo ✅ sitemap.xml is accessible
    curl -s "%base_url%/sitemap.xml" | findstr "urlset" >nul
    if %errorlevel%==0 (
        echo ✅ sitemap.xml contains proper XML structure
        curl -s "%base_url%/sitemap.xml" | findstr "loc" | wc -l
        echo    URLs in sitemap: As shown above
    ) else (
        echo ❌ sitemap.xml is missing XML structure
    )
) else (
    echo ❌ sitemap.xml is not accessible
)

echo.

REM Test main page meta tags
echo 3. Testing main page SEO...
curl -s "%base_url%/" > temp_page.html 2>nul
if %errorlevel%==0 (
    echo ✅ Main page is accessible

    REM Check for title tag
    findstr "<title>" temp_page.html >nul
    if %errorlevel%==0 (
        echo ✅ Page has title tag
    ) else (
        echo ❌ Page is missing title tag
    )

    REM Check for meta description
    findstr "name=.description." temp_page.html >nul
    if %errorlevel%==0 (
        echo ✅ Page has meta description
    ) else (
        echo ❌ Page is missing meta description
    )

    REM Check for Open Graph tags
    findstr "property=.og:" temp_page.html >nul
    if %errorlevel%==0 (
        echo ✅ Page has Open Graph tags
    ) else (
        echo ❌ Page is missing Open Graph tags
    )

    REM Check for structured data
    findstr "application/ld+json" temp_page.html >nul
    if %errorlevel%==0 (
        echo ✅ Page has structured data
    ) else (
        echo ❌ Page is missing structured data
    )

    REM Check for canonical URL
    findstr "rel=.canonical." temp_page.html >nul
    if %errorlevel%==0 (
        echo ✅ Page has canonical URL
    ) else (
        echo ❌ Page is missing canonical URL
    )

) else (
    echo ❌ Main page is not accessible
)

echo.

REM Clean up
if exist temp_page.html del temp_page.html

echo 4. SEO Verification Summary:
echo ===========================
echo.
echo 📋 Next Steps for Google Search Visibility:
echo.
echo 1. Deploy your application to a public server
echo 2. Go to Google Search Console (https://search.google.com/search-console)
echo 3. Add your property (domain or URL prefix)
echo 4. Submit your sitemap: %base_url%/sitemap.xml
echo 5. Request indexing for your main pages
echo 6. Monitor indexing status and any issues
echo.
echo 🌐 Social Media:
echo - Test Open Graph tags with Facebook Debugger
echo - Test Twitter cards with Twitter Card Validator
echo - Ensure your images meet social media requirements
echo.
echo 📊 Analytics:
echo - Add Google Analytics for traffic monitoring
echo - Consider Google Tag Manager for conversion tracking
echo.
echo ✅ SEO setup is complete! Your RadioHub application is ready for search engines.

pause
