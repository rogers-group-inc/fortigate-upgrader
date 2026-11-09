using FortiGateUpgrader.Core.Config;
using FortiGateUpgrader.Core.Models;

namespace FortiGateUpgrader.Tests;

/// <summary>Synthetic configs shaped like real exports. All names and addresses are fake.</summary>
internal static class Fixtures
{
    public static ProfileCatalog Catalog { get; } = ProfileCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Profiles"));

    public static ModelProfile Model(string m) => Catalog.FindByModel(m) ?? throw new InvalidOperationException(m);

    /// <summary>A flat (FortiManager preview style, no indentation) 61F config.</summary>
    public const string Config61F = """
#config-version=FGT61F-7.00-FW-build2829-000000:opmode=0:vdom=0:user=admin
#version=700
#build=2829
#platform=FORTIGATE-61F
#serialno=FGT61FTK00000000
#hwrev=P00000-1
#logdisk=1
#mgmt.data=00000000000000000000
config system global
set hostname "TESTPLANT-61F-1"
set timezone 12
end
config system virtual-switch
edit "internal"
set physical-switch "sw0"
config port
edit "internal1"
next
edit "internal2"
next
edit "internal3"
next
end
next
end
config system interface
edit "wan1"
set vdom "root"
set mode dhcp
set type physical
set role wan
set snmp-index 1
next
edit "wan2"
set vdom "root"
set mode dhcp
set type physical
set role wan
set speed 100full
set snmp-index 2
next
edit "dmz"
set vdom "root"
set ip 198.51.100.1 255.255.255.0
set type physical
set role dmz
set snmp-index 3
next
edit "internal1"
set vdom "root"
set type physical
set snmp-index 4
next
edit "internal2"
set vdom "root"
set type physical
set snmp-index 5
next
edit "internal3"
set vdom "root"
set type physical
set snmp-index 6
next
edit "internal4"
set vdom "root"
set type physical
set snmp-index 7
next
edit "internal5"
set vdom "root"
set type physical
set snmp-index 8
next
edit "a"
set vdom "root"
set type physical
set snmp-index 9
next
edit "b"
set vdom "root"
set type physical
set snmp-index 10
next
edit "modem"
set vdom "root"
set mode pppoe
set status down
set type physical
set snmp-index 11
next
edit "internal"
set vdom "root"
set ip 192.0.2.1 255.255.255.0
set type hard-switch
set role lan
set snmp-index 15
next
edit "fortilink"
set vdom "root"
set ip 203.0.113.1 255.255.255.0
set fortilink enable
set type aggregate
set member "a" "b"
set snmp-index 16
next
edit "Users"
set vdom "root"
set ip 10.99.10.1 255.255.255.0
set interface "fortilink"
set vlanid 10
next
edit "Overlay-1"
set vdom "root"
set type tunnel
set interface "wan1"
next
end
config system ha
set hbdev "a" 50
end
config firewall address
edit "dmz"
set type interface-subnet
set subnet 198.51.100.0 255.255.255.0
set associated-interface "dmz"
next
end
config firewall addrgrp
edit "Servers"
set member "dmz"
next
end
config system sdwan
set status enable
config members
edit 1
set interface "wan1"
next
edit 2
set interface "wan2"
next
end
end
config vpn certificate local
edit "TestCert"
set certificate "-----BEGIN CERTIFICATE-----
MIIBszCCAVmgAwIBAgIUZmFrZSBjZXJ0aWZpY2F0ZQ==
end
next
-----END CERTIFICATE-----"
next
end
config firewall policy
edit 1
set name "DMZ-out"
set srcintf "dmz"
set dstintf "virtual-wan-link"
set srcaddr "all"
set dstaddr "all"
set action accept
set schedule "always"
set service "ALL"
set comments "has a \"quoted\" word"
next
edit 2
set srcintf "internal"
set dstintf "virtual-wan-link"
set srcaddr "all"
set dstaddr "all"
set action accept
set schedule "always"
set service "ALL"
next
end
""";

    /// <summary>An indented 91G config containing FortiSwitch ports whose names collide with FortiGate ports.</summary>
    public const string Config91G = """
#config-version=FGT91G-7.00-FW-build3704-000000:opmode=0:vdom=0:user=admin
#build=3704
#platform=FORTIGATE-91G
#serialno=FGT91GTK00000000
config system global
    set hostname "TESTPLANT-91G-1"
end
config system interface
    edit "x1"
        set vdom "root"
        set mode dhcp
        set type physical
        set alias "WAN1"
        set role wan
    next
    edit "x2"
        set vdom "root"
        set type physical
    next
    edit "port1"
        set vdom "root"
        set ip 192.0.2.1 255.255.255.0
        set type physical
    next
    edit "port2"
        set vdom "root"
        set ip 198.51.100.1 255.255.255.0
        set type physical
    next
    edit "port3"
        set vdom "root"
        set type physical
    next
    edit "port4"
        set vdom "root"
        set type physical
    next
    edit "port5"
        set vdom "root"
        set type physical
    next
    edit "port6"
        set vdom "root"
        set type physical
    next
    edit "a"
        set vdom "root"
        set type physical
    next
    edit "b"
        set vdom "root"
        set type physical
    next
    edit "modem"
        set vdom "root"
        set mode pppoe
        set status down
        set type physical
    next
    edit "fortilink"
        set vdom "root"
        set ip 203.0.113.1 255.255.255.0
        set fortilink enable
        set type aggregate
        set member "x2"
    next
end
config firewall policy
    edit 1
        set srcintf "port1"
        set dstintf "port2"
        set srcaddr "all"
        set dstaddr "all"
        set action accept
        set schedule "always"
        set service "ALL"
    next
end
config switch-controller managed-switch
    edit "S124FTEST0000001"
        set fsw-wan1-peer "fortilink"
        config ports
            edit "port1"
                set vlan "_default"
            next
            edit "port2"
                set speed 1000full
            next
        end
    next
end
""";

    /// <summary>VPN additions for the 61F fixture. The ENC values are dummies.</summary>
    public const string Vpn61F = """
config vpn ipsec phase1-interface
edit "Overlay-1"
set interface "wan1"
set peertype any
set net-device enable
set proposal aes256-sha256
set localid "OLD-ID"
set remote-gw 192.0.2.254
set psksecret ENC AAAAdummyAAAA==
next
edit "Backup-1"
set interface "wan2"
set peertype any
set proposal aes256-sha256
set remote-gw 192.0.2.253
set psksecret ENC BBBBdummyBBBB==
next
edit "Cert-1"
set interface "wan1"
set authmethod signature
set certificate "TestCert"
set peertype any
set remote-gw 192.0.2.252
next
end
config vpn ipsec phase2-interface
edit "Overlay-1-p2"
set phase1name "Overlay-1"
next
edit "Backup-1-p2"
set phase1name "Backup-1"
next
end
config router static
edit 1
set dst 10.50.0.0 255.255.0.0
set device "Backup-1"
next
edit 2
set dst 10.60.0.0 255.255.0.0
set device "Overlay-1"
next
end
""";

    public static FortiConfig Parse61FWithVpn() => FortiConfigParser.Parse(Config61F + Environment.NewLine + Vpn61F);

    public static FortiConfig Parse61F() => FortiConfigParser.Parse(Config61F);
    public static FortiConfig Parse91G() => FortiConfigParser.Parse(Config91G);

    public static readonly Dictionary<string, string> Map61Fto91G = new()
    {
        ["wan1"] = "x1", ["wan2"] = "x2", ["dmz"] = "port6",
        ["internal1"] = "port1", ["internal2"] = "port2", ["internal3"] = "port3",
        ["internal4"] = "port4", ["internal5"] = "port5", ["a"] = "a", ["b"] = "b",
    };

    /// <summary>Builds a minimal config for any profile in which every port has an IP (so all are in use).</summary>
    public static FortiConfig AllPortsInUse(ModelProfile model)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"#config-version={model.ConfigPrefix}-7.00-FW-build9999-000000:opmode=0:vdom=0:user=admin\n#platform={model.Platform}\n");
        sb.Append("config system global\nset hostname \"T\"\nend\nconfig system interface\n");
        var i = 1;
        foreach (var p in model.Ports)
            sb.Append($"edit \"{p.Name}\"\nset vdom \"root\"\nset ip 10.0.{i++}.1 255.255.255.0\nset type physical\nnext\n");
        sb.Append("end\nconfig firewall policy\n");
        var n = 1;
        foreach (var p in model.Ports)
            sb.Append($"edit {n++}\nset srcintf \"{p.Name}\"\nset dstintf \"{model.Ports[0].Name}\"\nset srcaddr \"all\"\nset dstaddr \"all\"\nset action accept\nset schedule \"always\"\nset service \"ALL\"\nnext\n");
        sb.Append("end\n");
        return FortiConfigParser.Parse(sb.ToString());
    }
}
