using System;
using System.Collections.Generic;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Xunit;

namespace Swarm.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void DefaultSettings_HaveSafeDefaults()
    {
        var settings = new Settings();

        Assert.True(settings.IsSyncEnabled);
        Assert.False(settings.AutoAcceptFromTrusted);
        Assert.False(settings.StartMinimized);
        Assert.NotNull(settings.SyncFolderPath);
        Assert.NotNull(settings.ExcludedFolders);
        Assert.Empty(settings.ExcludedFolders);
    }

    [Fact]
    public void TrustPeer_AddsPeerAndKey()
    {
        var settings = new Settings();
        var peer = new Peer 
        { 
            Id = "test-peer", 
            Name = "Test Peer",
            PublicKeyBase64 = "dGVzdC1rZXk=" 
        };

        settings.TrustPeer(peer);

        Assert.Contains(settings.TrustedPeers, p => p.Id == "test-peer");
        Assert.Contains(settings.TrustedPeerPublicKeys, k => k.Key == "test-peer" && k.Value == "dGVzdC1rZXk=");
        Assert.True(peer.IsTrusted);
    }

    [Fact]
    public void TrustPeer_DuplicateHost_DoesNotDuplicateEntry()
    {
        var settings = new Settings();
        var peer = new Peer 
        { 
            Id = "test-peer", 
            Name = "Test Peer",
            PublicKeyBase64 = "dGVzdC1rZXk=" 
        };

        settings.TrustPeer(peer);
        settings.TrustPeer(peer);

        Assert.Single(settings.TrustedPeers);
        Assert.Single(settings.TrustedPeerPublicKeys);
    }

    [Fact]
    public void UntrustPeer_RemovesPeerAndKey()
    {
        var settings = new Settings();
        var peer = new Peer 
        { 
            Id = "test-peer", 
            Name = "Test Peer",
            PublicKeyBase64 = "dGVzdC1rZXk=" 
        };

        settings.TrustPeer(peer);
        settings.UntrustPeer("test-peer");

        Assert.DoesNotContain(settings.TrustedPeers, p => p.Id == "test-peer");
        Assert.DoesNotContain(settings.TrustedPeerPublicKeys, k => k.Key == "test-peer");
    }

    [Fact]
    public void Clone_CreatesDeepCopy()
    {
        var settings = new Settings
        {
            DeviceName = "Original",
            IsSyncEnabled = true
        };
        settings.ExcludedFolders.Add("Folder1");
        settings.TrustedPeers.Add(new TrustedPeer { Id = "p1", Name = "P1" });

        var clone = settings.Clone();

        Assert.Equal(settings.DeviceName, clone.DeviceName);
        Assert.Equal(settings.IsSyncEnabled, clone.IsSyncEnabled);
        Assert.Single(clone.ExcludedFolders);
        Assert.Equal("Folder1", clone.ExcludedFolders[0]);
        Assert.Single(clone.TrustedPeers);
        
        // Modify clone, original should not change
        clone.DeviceName = "Modified";
        clone.ExcludedFolders.Add("Folder2");

        Assert.NotEqual(settings.DeviceName, clone.DeviceName);
        Assert.Single(settings.ExcludedFolders);
        Assert.Equal(2, clone.ExcludedFolders.Count);
    }

    [Fact]
    public void UpdateFrom_CopiesValues()
    {
        var target = new Settings
        {
            DeviceName = "Target",
            IsSyncEnabled = false
        };

        var source = new Settings
        {
            DeviceName = "Source",
            IsSyncEnabled = true
        };
        source.ExcludedFolders.Add("ExcludeMe");

        target.UpdateFrom(source);

        Assert.Equal("Source", target.DeviceName);
        Assert.True(target.IsSyncEnabled);
        Assert.Single(target.ExcludedFolders);
        Assert.Equal("ExcludeMe", target.ExcludedFolders[0]);
    }
}
