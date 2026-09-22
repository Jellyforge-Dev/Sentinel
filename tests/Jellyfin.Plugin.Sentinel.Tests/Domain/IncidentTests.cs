using System;
using Jellyfin.Plugin.Sentinel.Domain;
using Xunit;

namespace Jellyfin.Plugin.Sentinel.Tests.Domain;

public class IncidentTests
{
    [Fact]
    public void Incident_RoundTripsAllProperties()
    {
        var firstSeen = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastSeen = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var acknowledged = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);

        var incident = new Incident
        {
            Id = 1,
            Code = "TEST_CODE",
            ItemId = "item-1",
            Client = "Fire TV",
            DeviceName = "Living Room",
            UserName = "Alice",
            Status = IncidentStatus.Acknowledged,
            OccurrenceCount = 3,
            FirstSeenUtc = firstSeen,
            LastSeenUtc = lastSeen,
            AcknowledgedAtUtc = acknowledged,
            ResolvedAtUtc = null
        };

        Assert.Equal(1, incident.Id);
        Assert.Equal("TEST_CODE", incident.Code);
        Assert.Equal("item-1", incident.ItemId);
        Assert.Equal("Fire TV", incident.Client);
        Assert.Equal("Living Room", incident.DeviceName);
        Assert.Equal("Alice", incident.UserName);
        Assert.Equal(IncidentStatus.Acknowledged, incident.Status);
        Assert.Equal(3, incident.OccurrenceCount);
        Assert.Equal(firstSeen, incident.FirstSeenUtc);
        Assert.Equal(lastSeen, incident.LastSeenUtc);
        Assert.Equal(acknowledged, incident.AcknowledgedAtUtc);
        Assert.Null(incident.ResolvedAtUtc);
    }
}
