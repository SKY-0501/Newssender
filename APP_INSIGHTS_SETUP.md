# Application Insights Real User Monitoring Setup

## Overview
This project now includes **Application Insights JavaScript SDK** for Real User Monitoring (RUM). This captures user engagement metrics, page performance, and application errors.

## What's Configured

### 1. **Application Insights SDK Initialization**
- **Instrumentation Key**: `6f8ab2d0-e0c7-4ea2-8169-5edf9242551f`
- **Ingestion Endpoint**: `https://eastus-8.in.applicationinsights.azure.com/`
- **Region**: East US

The SDK is initialized in all HTML pages with `appInsights.trackPageView()` to track every page load.

### 2. **Pages with Instrumentation**
The following pages are configured to send telemetry data:
- ✅ `index.html` - Newsletter landing page
- ✅ `form.html` - Contact form
- ✅ `subscribe.html` - Subscribe form
- ✅ `newsletter-signup.html` - Newsletter signup
- ✅ `secondform.html` - Conversation starter form
- ✅ `thank-you.html` - Thank you confirmation page
- ✅ `message_received.html` - Message received confirmation
- ✅ `unsubscribe.html` - Unsubscribe page
- ✅ `confirm-subscription.html` - Subscription confirmation page

### 3. **Custom Events Tracking** (`app-insights-tracking.js`)

The custom tracking script captures the following user interactions:

#### **Form Submissions**
- Event: `FormSubmitted`
- Properties: Form name, timestamp, page URL
- Captures every form submission across the site

#### **Button Clicks**
- Event: `ButtonClicked`
- Properties: Button text, button ID, page URL
- Captures all button clicks on submit buttons

#### **Link Clicks**
- Event: `LinkClicked`
- Properties: Link text, link URL, whether external link, page URL
- Automatically tracks if link is external or internal

#### **Scroll Depth**
- Event: `ScrollDepth`
- Properties: Scroll depth percentage (25%, 50%, 75%, 100%), page URL, page title
- Tracks user engagement depth on each page

#### **Page Load Performance**
- Event: `PageLoadMetrics`
- Properties: Page load time (ms), DOM content loaded time (ms), page URL
- Captures page performance metrics

#### **Errors & Exceptions**
- Event: `Exception` (from JS errors)
- Event: `UnhandledPromiseRejection` (from async errors)
- Automatically tracks and reports all JavaScript errors

## Metrics You'll See in Azure Portal

In Azure Application Insights, you can now monitor:

### Real User Monitoring (RUM)
1. **Users** - Number of unique visitors
2. **Sessions** - User sessions and duration
3. **Page Views** - Which pages are visited most
4. **Bounce Rate** - Percentage of single-page sessions
5. **Device Types** - Desktop, mobile, tablet breakdown
6. **Browser Types** - Chrome, Firefox, Safari, Edge usage
7. **Operating Systems** - Windows, macOS, iOS, Android breakdown
8. **Geographic Location** - Where users are connecting from

### User Behavior
1. **Form Completion Rate** - How many users submit forms
2. **Pages Visited** - User navigation patterns
3. **Conversion Funnel** - From subscribe → thank you page
4. **Unsubscribe Rate** - Users opting out
5. **Click Tracking** - Most clicked elements
6. **Scroll Engagement** - How far down pages users scroll

### Performance Metrics
1. **Page Load Time** - Overall page load performance
2. **DOM Content Loaded** - Time before page is interactive
3. **Time to Interactive** - When page is fully functional

### Errors & Issues
1. **JavaScript Errors** - Uncaught exceptions
2. **Failed Form Submissions** - Error tracking
3. **Failed API Calls** - Dependency tracking
4. **Browser Compatibility Issues** - Error patterns by browser

## How to View Data

1. **Azure Portal**
   - Go to your Application Insights resource
   - Navigate to "User flows" to see navigation paths
   - Check "Performance" for load times
   - View "Failures" for errors and exceptions
   - Check "Users" section for demographics

2. **Queries (Kusto Query Language)**
   ```kusto
   // Top pages visited
   pageViews | summarize count() by url

   // Form submission events
   customEvents | where name == "FormSubmitted" | summarize count()

   // User locations
   pageViews | summarize count() by client_CountryOrRegion
   ```

3. **Dashboards**
   - Create custom dashboards in Azure Portal
   - Add tiles for key metrics
   - Monitor in real-time

## Environment Configuration

- **Development**: Data is sent to Azure Application Insights immediately
- **Production**: Same endpoint is used
- **No API Keys Required**: JavaScript SDK uses Connection String for security

## Best Practices

1. **Form Tracking**: Forms automatically tracked when they have `id` or `name` attributes
2. **Custom Events**: Can add more tracking by calling:
   ```javascript
   window.appInsights.trackEvent({
     name: 'EventName',
     properties: { key: 'value' }
   });
   ```

3. **Error Tracking**: Errors are automatically caught, but can also send custom errors:
   ```javascript
   window.appInsights.trackException({
     exception: new Error('Custom error message'),
     properties: { severity: 'critical' }
   });
   ```

4. **Performance**: The tracking script loads with `defer` attribute, ensuring it doesn't block page rendering

## Troubleshooting

### Data Not Appearing in Azure Portal
1. Check that telemetry is being sent:
   - Open browser DevTools → Network tab
   - Look for requests to `in.applicationinsights.azure.com`
   - If not present, the SDK may have failed to initialize

2. Verify Connection String:
   - Check that the instrumentation key matches your Azure resource
   - Connection string should include all endpoints

3. Check for JavaScript Errors:
   - Open browser console for any errors
   - Ensure `app-insights-tracking.js` loads successfully

### High Cookie/Storage Usage
- Application Insights uses minimal storage
- Can be configured further if needed

## Next Steps

1. **Set Up Alerts**: Configure alerts for high error rates or performance issues
2. **Create Synthetic Monitors**: Monitor key user flows from different regions
3. **Configure Retention**: Adjust data retention policies as needed
4. **Integrate with Other Services**: Connect to Azure Pipelines, Teams, etc.

## References

- [Microsoft Docs: Application Insights JavaScript SDK](https://learn.microsoft.com/en-us/azure/azure-monitor/app/javascript-sdk)
- [Real User Monitoring Guide](https://learn.microsoft.com/en-us/azure/azure-monitor/app/javascript-sdk?tabs=javascriptwebsdkloaderscript)
- [Kusto Query Language](https://learn.microsoft.com/en-us/azure/kusto/query/)

---

**Setup Complete!** Your application is now collecting Real User Monitoring data. Check Azure Portal in 5-10 minutes to see the first telemetry data appearing.
