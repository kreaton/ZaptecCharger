# Prompt

create a .net 10 windows ui solution that shows the status of my zaptec charger using their api and can start or stop it. put the user information in a settings file. make a separate dll for the zaptec api stuff. can it receive status updates or would it have to poll

# Reply

I'll check Zaptec's API docs and whether .NET 10 is available here, then build the solution.

The solution is in ZaptecCharger.zip, but I couldn't build it. The .NET 10 SDK can't be installed in my sandbox, so run `dotnet build` once and tell me if anything fails.

**What's in it**
- **`Zaptec.Api`** is a separate `net10.0` DLL. It logs in with your Zaptec portal username and password and caches the token. It lists chargers, reads their state, and sends stop and start commands.
- **`ZaptecMonitor`** is a WPF app on `net10.0-windows`. It shows the charger name, status, power in kW, session kWh and online/offline. It has Start and Stop buttons.
- **Settings** are in `appsettings.json` next to the exe: `Username`, `Password`, an optional `ChargerId` (the first charger is used if empty) and `PollIntervalSeconds`. The password is stored in plain text, so keep that file private.

**Start and Stop**
- Zaptec only accepts Stop (command 506) while the car is actively charging.
- It only accepts Start (command 507) when charging was previously paused through the API.
- The buttons enable themselves to match those rules.
- If the car simply stopped drawing power, Start stays disabled, because Zaptec rejects it in that state.

**Polling or push**
- Zaptec does offer push updates, through an Azure Service Bus (AMQP) message subscription.
- Zaptec's docs say a subscription has to be enabled on your user group through a support request.
- Subscriptions are removed automatically after 14 days without use, and the connection details can change, so you have to fetch them from the API each time.
- Otherwise it has to poll. The app polls every 15 seconds by default, with a 5-second minimum.
- If you get the subscription enabled, I can add a listener in the DLL using `Azure.Messaging.ServiceBus` and keep polling as a fallback.

The login and state calls use `/oauth/token` and `/api/chargers/{id}/state`. I wrote those from memory of the API, not from the current docs, so they are the first thing to check if something fails. The command IDs and state IDs come from Zaptec's current docs.

Sources: [Zaptec send command docs](https://docs.zaptec.com/reference/api_chargers_id_sendcommand_commandid_post), [pause/resume guidance](https://docs.zaptec.com/docs/approaches-to-start-and-stop-charging-during-a-session), [Service Bus subscriptions](https://docs.zaptec.com/docs/zaptec-service-bus-subscriptions)