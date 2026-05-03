# Essential Project Keys & Configuration Reference

This file serves as a central reference for all essential keys, connection strings, and configuration variables used across the **Orchvate Newsletter Sender** project.

## 🔑 Backend Environment Variables (.env)
These are used for database connectivity, email services, and analytics tracking.

| Key | Description | Value |
| :--- | :--- | :--- |
| `ACS__ConnectionString` | Azure Communication Services for sending emails | `endpoint=https://orchvate...` |
| `Database__PostgresConnectionString` | Connection string for Azure PostgreSQL database | `Host=generative-ai...` |
| `PowerAutomate__Url` | Flow URL for sending emails via Power Automate | `https://default...` |
| `AppInsights__InstrumentationKey` | Key for JS SDK to send telemetry to Azure | `6f8ab2d0-e0c7-4ea2-8169-5edf9242551f` |
| `AppInsights__WorkspaceId` | Workspace ID used for KQL Analytics queries | `575791d2-073a-4ad7-b407-f19079b56ae4` |

## 🛡️ Security & Authentication
Configuration for Microsoft Identity (Azure AD) and internal safety checks.

| Key | Description | Source | Value |
| :--- | :--- | :--- | :--- |
| `SafetyPassword` | Internal password required to trigger email blasts | `Program.cs` | `ECHO12345` |
| `AzureAd:TenantId` | Azure AD Tenant ID for Orchvate | `appsettings.json` | `ea77fe37-fd2c-4294-87c3-8746bce6625a` |
| `AzureAd:ClientId` | Application ID for the Newsletter Portal | `appsettings.json` | `c0494c0f-d339-44aa-b25c-33713cac77d6` |

## 🌐 Frontend Tracking
Commonly used IDs in the HTML/JS frontend.

| Key | Purpose | Found In |
| :--- | :--- | :--- |
| `InstrumentationKey` | Telemetry tracking on all pages | `APP_INSIGHTS_SETUP.md` |
| `allowedEmails` | Hardcoded list of users allowed to send emails | `Program.cs` (`aakash.padyachi@orchvate.com`, `rahul.rajesh@orchvate.com`) |

---
*Note: Keep this file secure as it contains sensitive connection strings.*
