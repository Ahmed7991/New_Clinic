# Clinic Queue Management System — Architecture Reference

## Project Structure

```
ClinicApi/
├── Program.cs                          # DI, middleware, SignalR mapping
├── ClinicApi.csproj                    # .NET 8, EF Core, Npgsql, SignalR
├── appsettings.json                    # Config template (fill in secrets)
├── Models/Entities.cs                  # EF Core entities
├── Data/ClinicDbContext.cs             # DbContext + indexes + seed data
├── DTOs/Dtos.cs                       # Request/response models
├── Services/
│   ├── BookingService.cs              # Slot assignment + race condition lock
│   ├── WhatsAppFunnelService.cs       # AI conversation state machine
│   ├── OpenRouterService.cs           # AI name validation (Arabic prompt)
│   ├── WhatsAppSender.cs              # Meta Cloud API sender
│   └── NotificationService.cs         # Day-before + approaching alerts
├── Controllers/
│   ├── WhatsAppWebhookController.cs   # Meta webhook handler
│   ├── QueueController.cs             # Secretary + Doctor dashboard API
│   └── CalendarSettingsController.cs  # Calendar blocks + clinic settings
├── Hubs/QueueHub.cs                   # SignalR real-time hub
└── Jobs/NightlyQueueResetJob.cs       # Midnight cleanup + reminders
```

---

## API Endpoints

### WhatsApp Webhook
| Method | Endpoint                     | Purpose                           |
|--------|------------------------------|-----------------------------------|
| GET    | `/api/webhook/whatsapp`      | Meta webhook verification         |
| POST   | `/api/webhook/whatsapp`      | Incoming message handler          |

### Queue Management (Secretary Dashboard)
| Method | Endpoint                     | Purpose                           |
|--------|------------------------------|-----------------------------------|
| GET    | `/api/queue/today`           | Today's full queue                |
| GET    | `/api/queue/{date}`          | Queue for a specific date         |
| PUT    | `/api/queue/{id}/status`     | Change status (Pending→InRoom…)   |
| POST   | `/api/queue/walkin`          | Add walk-in patient               |
| DELETE | `/api/queue/{id}`            | Cancel appointment                |

### Doctor Dashboard
| Method | Endpoint                     | Purpose                           |
|--------|------------------------------|-----------------------------------|
| GET    | `/api/queue/doctor-view`     | Current patient + Up Next only    |

### Calendar
| Method | Endpoint                     | Purpose                           |
|--------|------------------------------|-----------------------------------|
| GET    | `/api/calendar/{year}/{month}` | Calendar overrides for a month  |
| POST   | `/api/calendar/block`        | Block a day                       |
| POST   | `/api/calendar/unblock`      | Unblock a day                     |

### Settings
| Method | Endpoint                     | Purpose                           |
|--------|------------------------------|-----------------------------------|
| GET    | `/api/settings`              | Get clinic settings               |
| PUT    | `/api/settings`              | Update settings                   |

### SignalR Hub
| Endpoint          | Client Events                                        |
|-------------------|------------------------------------------------------|
| `/hubs/queue`     | `QueueUpdated`, `StatusChanged`, `NewBooking`, `DoctorViewUpdated` |

---

## Key Design Decisions

### Race Condition Handling
`BookingService` uses a `SemaphoreSlim(1,1)` to serialize slot assignment. A booking
is only confirmed after name + phone are fully validated. First to complete wins.

### Slot Calculation
`EstimatedStart = DayStartTime + (QueuePosition - 1) × AvgConsultationMinutes`

### WhatsApp 24-Hour Rule
For future appointments, `NightlyQueueResetJob` sends a day-before reminder asking
the patient to reply "Yes". Their reply reopens Meta's 24-hour messaging window,
allowing the system to send same-day approaching alerts.

### AI Funnel State Machine
```
AwaitingName → AwaitingPhone → [AwaitingReturningUserChoice] → Completed
```
The AI (via OpenRouter) only handles Arabic name validation. Phone extraction and
returning-user logic are deterministic C# code — not AI.

---

## Getting Started

```bash
# 1. Create the project
dotnet new webapi -n ClinicApi
# (replace generated files with provided code)

# 2. Restore packages
dotnet restore

# 3. Configure appsettings.json with your secrets

# 4. Create initial migration
dotnet ef migrations add InitialCreate

# 5. Apply migration
dotnet ef database update

# 6. Run
dotnet run
```

Swagger UI available at `https://localhost:5001/swagger` in development.
