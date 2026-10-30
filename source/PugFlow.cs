using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace PugFlow;

/// <summary>
/// Warmup -> chat vote -> knife round -> winning team picks a side (!ct/!t or !stay/!swap) -> live MR12.
/// On Rush maps: the same warmup and vote, then straight to a live Rush match (no knife round).
/// Independent of RockTheVote-CSS. Built against the server's own CSS API DLL.
/// </summary>
public sealed class PugFlowPlugin : BasePlugin
{
    public override string ModuleName => "PugFlow";
    public override string ModuleVersion => "1.6.0";
    public override string ModuleAuthor => "CS2 community server";
    public override string ModuleDescription => "Free warmup, start vote, knife round, winning-team side pick, live match";

    private const int MinimumPlayers = 1;
    private const double ApprovalFraction = 0.50;
    private const float StartVoteSeconds = 25f;
    private const int StartVoteCooldownSeconds = 30;
    private const float SidePickSeconds = 30f;
    // Longer than RockTheVote's DelayToChangeInTheEnd (default 6s) so its map change goes first.
    private const float MatchEndReloadSeconds = 15f;
    private const string ResetPermission = "@css/generic";
    private const int DropKnifeCount = 5;
    // Warmup is one endless round, so outside a live match .drop uses a time cooldown.
    private const int DropCooldownSeconds = 30;

    // Game time only resets on a map change. After hours on one map it is large enough to lose
    // float precision and weapon/hand animations play in slow motion, so an empty server
    // reloads its map once the map has run this long.
    private const float StaleMapSeconds = 3600f;
    private const float StaleMapCheckSeconds = 60f;

    // RoundEndReason.GameCommencing: round_end fired by a restart, not by a real round result.
    private const int GameCommencingReason = 16;

    // Mirror configs/plugins/RockTheVote/RockTheVote.json so !help matches what RockTheVote
    // allows; update these if that config changes.
    private const int RtvMapStartDelaySeconds = 180;
    private static readonly string[] NominatePermissions = { "@css/vip", "@css/nominate" };
    private const string MapMenuPermission = "@css/changemap";

    // Rush maps run Valve's own Rush rules (the ModeSwitcher plugin sets game_mode 6 for them).
    // They get the same free warmup and start vote, but no knife round, and go live with
    // rush_live.cfg instead of live.cfg. Keep in sync with ModeSwitcher's list.
    private static readonly string[] RushMaps = { "rush_001" };

    private enum MatchPhase { Warmup, Voting, KnifePending, KnifeActive, SidePick, Live }
    private MatchPhase _phase = MatchPhase.Warmup;
    private readonly HashSet<ulong> _yes = new();
    private readonly HashSet<ulong> _no = new();
    private readonly HashSet<ulong> _knifeWinners = new();
    private int _knifeWinnerTeam;
    private int _voteSerial;
    private int _sideSerial;
    private DateTimeOffset _lastVoteFailed = DateTimeOffset.MinValue;
    private int _roundSerial;
    private readonly Dictionary<ulong, int> _dropRound = new();
    private readonly Dictionary<ulong, DateTimeOffset> _dropTime = new();
    private DateTimeOffset _mapStarted = DateTimeOffset.MinValue;

    // Unavailable returns null when the player can use the command right now, else a short reason.
    private sealed record PlayerCommand(string Name, string Description, Func<CCSPlayerController, string?> Unavailable);

    public override void Load(bool hotReload)
    {
        AddCommand("css_votestart", "Vote to start a knife round then a live match", StartVote);
        AddCommand("css_yes", "Vote YES to start", VoteYes);
        AddCommand("css_no", "Vote NO to start", VoteNo);
        AddCommand("css_startyes", "Vote YES to start", VoteYes);
        AddCommand("css_startno", "Vote NO to start", VoteNo);
        AddCommand("css_stay", "Knife winner keeps their current side", Stay);
        AddCommand("css_swap", "Knife winner swaps CT and T sides", Swap);
        AddCommand("css_ct", "Knife winner takes the CT side", PickCT);
        AddCommand("css_t", "Knife winner takes the T side", PickT);
        AddCommand("css_help", "List player commands and whether they work right now", Help);
        AddCommand("css_commands", "List player commands and whether they work right now", Help);
        AddCommand("css_startstatus", "Show match phase / vote status", Status);
        AddCommand("css_startreset", "Admin: abort vote/knife/match and restore free warmup", ForceReset);
        AddCommandListener("say", OnChat);
        AddCommandListener("say_team", OnChat);

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        // Not STOP_ON_MAPCHANGE: this one runs for the plugin's whole lifetime.
        AddTimer(StaleMapCheckSeconds, RefreshStaleMap, TimerFlags.REPEAT);
        // Restart the cs2 systemd service after deploying, rather than hot-reloading
        // into a live match; this plugin doesn't guess the state of an existing match.
    }

    private static bool OnRushMap() => RushMaps.Contains(Server.MapName, StringComparer.OrdinalIgnoreCase);

    private static bool Human(CCSPlayerController? p) =>
        p is { IsValid: true } && !p.IsBot && !p.IsHLTV && p.SteamID != 0;

    private static List<CCSPlayerController> Humans(CCSPlayerController? except = null) =>
        Utilities.GetPlayers()
            .Where(x => Human(x) && (except == null || x.SteamID != except.SteamID))
            .ToList();

    // Everyone on a playing side, bots included, so a swap moves whole teams.
    private static List<CCSPlayerController> TeamPlayers() =>
        Utilities.GetPlayers()
            .Where(x => x is { IsValid: true } && !x.IsHLTV
                && (x.Team == CsTeam.Terrorist || x.Team == CsTeam.CounterTerrorist))
            .ToList();

    private static bool InWarmup()
    {
        try
        {
            var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;
            return rules?.WarmupPeriod ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static int NeededYes(int count) =>
        Math.Max(1, (int)Math.Ceiling(count * ApprovalFraction));

    private void ClearBallots()
    {
        _yes.Clear();
        _no.Clear();
        ++_voteSerial;
    }

    // Drops ballots of players who left; returns the number of humans still online.
    private int PruneBallots(CCSPlayerController? leaving = null)
    {
        var online = Humans(leaving).Select(x => x.SteamID).ToHashSet();
        _yes.RemoveWhere(x => !online.Contains(x));
        _no.RemoveWhere(x => !online.Contains(x));
        return online.Count;
    }

    private void StartVote(CCSPlayerController? p, CommandInfo info)
    {
        if (!Human(p)) return;
        if (_phase != MatchPhase.Warmup)
        {
            p!.PrintToChat("[PugFlow] Only available during free warmup. !startstatus");
            return;
        }

        var online = Humans();
        if (online.Count < MinimumPlayers)
        {
            p!.PrintToChat($"[PugFlow] Need at least {MinimumPlayers} human player(s).");
            return;
        }
        // Cooldown counts from when the previous vote failed, not from when it started.
        var remaining = StartVoteCooldownSeconds - (DateTimeOffset.UtcNow - _lastVoteFailed).TotalSeconds;
        if (remaining > 0)
        {
            p!.PrintToChat($"[PugFlow] Wait {Math.Ceiling(remaining)}s before trying again.");
            return;
        }

        _phase = MatchPhase.Voting;
        ClearBallots();
        var serial = _voteSerial;

        Server.PrintToChatAll($"[PugFlow] {p!.PlayerName} started a match vote! {NeededYes(online.Count)}/{online.Count} YES needed (50%).");
        Server.PrintToChatAll($"[PugFlow] Type !yes or !no within {(int)StartVoteSeconds} seconds. Proposer must also vote.");
        AddTimer(StartVoteSeconds, () =>
        {
            if (_phase != MatchPhase.Voting || serial != _voteSerial) return;
            FailVote("Time ran out. Free warmup continues.");
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void VoteYes(CCSPlayerController? p, CommandInfo info) => RecordVote(p, true);
    private void VoteNo(CCSPlayerController? p, CommandInfo info) => RecordVote(p, false);

    private void RecordVote(CCSPlayerController? p, bool yes)
    {
        if (!Human(p)) return;
        if (_phase != MatchPhase.Voting)
        {
            p!.PrintToChat("[PugFlow] No open start vote. Use !votestart in warmup.");
            return;
        }

        var id = p!.SteamID;
        if (_yes.Contains(id) || _no.Contains(id))
        {
            p.PrintToChat("[PugFlow] Your vote is already counted.");
            return;
        }
        if (yes) _yes.Add(id); else _no.Add(id);

        int online = PruneBallots();
        Server.PrintToChatAll($"[PugFlow] YES {_yes.Count}, NO {_no.Count} | {NeededYes(online)} YES needed ({online} online).");
        EvaluateVote(online);
    }

    private void EvaluateVote(int online)
    {
        int needed = NeededYes(online);
        if (_yes.Count >= needed)
        {
            ClearBallots();
            BeginKnifeOrSoloLive();
        }
        else if (_yes.Count + (online - _yes.Count - _no.Count) < needed)
        {
            FailVote("Not enough remaining YES votes. Free warmup continues.");
        }
    }

    private void FailVote(string message)
    {
        ClearBallots();
        _phase = MatchPhase.Warmup;
        _lastVoteFailed = DateTimeOffset.UtcNow;
        Server.PrintToChatAll($"[PugFlow] {message}");
    }

    private void BeginKnifeOrSoloLive()
    {
        if (OnRushMap())
        {
            // Rush has no halftime, so there is no side to pick and no knife round.
            _phase = MatchPhase.Live;
            Server.PrintToChatAll("[PugFlow] Vote passed! RUSH is LIVE: 15 rounds, first to 8 wins. !help lists commands.");
            Server.ExecuteCommand("exec rush_live.cfg");
            return;
        }

        var teams = Humans();
        bool hasT = teams.Any(x => x.Team == CsTeam.Terrorist);
        bool hasCT = teams.Any(x => x.Team == CsTeam.CounterTerrorist);
        if (!hasT || !hasCT)
        {
            // Can't determine a winner with one side empty. Preserve the
            // one-person testing workflow and begin live without a knife round.
            _phase = MatchPhase.Live;
            Server.PrintToChatAll("[PugFlow] Vote passed! No human opponents on both sides: skipping knife round.");
            Server.PrintToChatAll("[Match] 12:12 goes to MR3 overtime, repeated until a team wins. !help lists commands.");
            Server.ExecuteCommand("exec live.cfg");
            return;
        }

        _phase = MatchPhase.KnifePending;
        _knifeWinnerTeam = 0;
        _knifeWinners.Clear();
        Server.PrintToChatAll("[PugFlow] Vote passed! KNIFE ROUND: knives only. Winners pick a side with !ct / !t (or !stay / !swap).");
        Server.ExecuteCommand("exec knife.cfg");
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart e, GameEventInfo info)
    {
        ++_roundSerial;
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd e, GameEventInfo info)
    {
        // knife.cfg's mp_warmup_end / mp_restartgame fire extra round_start and
        // round_end events. Only a round that gets past freeze time is the real fight.
        if (_phase == MatchPhase.KnifePending && !InWarmup())
        {
            _phase = MatchPhase.KnifeActive;
            Server.PrintToChatAll("[Knife] Fight! Knives only. Winning team picks sides after this round.");
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn e, GameEventInfo info)
    {
        var p = e.Userid;
        if (!Human(p) || (_phase != MatchPhase.KnifePending && _phase != MatchPhase.KnifeActive))
            return HookResult.Continue;
        var id = p!.SteamID;
        // Wait until the engine has assigned default equipment before stripping it.
        AddTimer(0.2f, () =>
        {
            if (_phase != MatchPhase.KnifePending && _phase != MatchPhase.KnifeActive) return;
            var current = Humans().FirstOrDefault(x => x.SteamID == id);
            if (current == null || (current.Team != CsTeam.Terrorist && current.Team != CsTeam.CounterTerrorist)) return;
            try
            {
                current.RemoveWeapons();
                current.GiveNamedItem("weapon_knife");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PugFlow] Knife weapon assignment failed for {id}: {ex}");
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd e, GameEventInfo info)
    {
        if (_phase != MatchPhase.KnifeActive) return HookResult.Continue;

        if (e.Reason == GameCommencingReason)
        {
            // Someone restarted the game mid-knife; wait for the restarted round's freeze end.
            _phase = MatchPhase.KnifePending;
            return HookResult.Continue;
        }

        if (e.Winner != (int)CsTeam.Terrorist && e.Winner != (int)CsTeam.CounterTerrorist)
        {
            _phase = MatchPhase.KnifePending;
            Server.PrintToChatAll("[Knife] Draw. Replaying knife round.");
            AddTimer(2f, () =>
            {
                if (_phase == MatchPhase.KnifePending) Server.ExecuteCommand("exec knife.cfg");
            }, TimerFlags.STOP_ON_MAPCHANGE);
            return HookResult.Continue;
        }

        _knifeWinnerTeam = e.Winner;
        _knifeWinners.Clear();
        foreach (var player in Humans().Where(x => (int)x.Team == _knifeWinnerTeam))
            _knifeWinners.Add(player.SteamID);
        _phase = MatchPhase.SidePick;
        int serial = ++_sideSerial;
        string side = _knifeWinnerTeam == (int)CsTeam.CounterTerrorist ? "CT" : "T";
        Server.PrintToChatAll($"[Knife] {side} won! A player from the winning team picks a side: !ct or !t (or !stay / !swap).");
        Server.PrintToChatAll($"[Knife] {SidePickSeconds:0}s to choose; timeout defaults to STAY.");

        // Keep the server in warmup while waiting for the choice, instead
        // of inadvertently playing a second knife round.
        AddTimer(1f, () =>
        {
            if (_phase == MatchPhase.SidePick && _sideSerial == serial)
                Server.ExecuteCommand("exec practice.cfg");
        }, TimerFlags.STOP_ON_MAPCHANGE);
        AddTimer(SidePickSeconds, () =>
        {
            if (_phase == MatchPhase.SidePick && _sideSerial == serial)
            {
                Server.PrintToChatAll("[Knife] Side choice timeout. Keeping sides.");
                StartLive(swap: false);
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    private void Stay(CCSPlayerController? p, CommandInfo info) => ChooseSide(p, swap: false);
    private void Swap(CCSPlayerController? p, CommandInfo info) => ChooseSide(p, swap: true);
    private void PickCT(CCSPlayerController? p, CommandInfo info) => ChooseTeam(p, CsTeam.CounterTerrorist);
    private void PickT(CCSPlayerController? p, CommandInfo info) => ChooseTeam(p, CsTeam.Terrorist);

    // !ct / !t name the side the knife winners want to play; that is a swap
    // exactly when it differs from the side they won the knife round on.
    private void ChooseTeam(CCSPlayerController? p, CsTeam side) =>
        ChooseSide(p, swap: (int)side != _knifeWinnerTeam);

    private void ChooseSide(CCSPlayerController? p, bool swap)
    {
        if (!Human(p)) return;
        if (_phase != MatchPhase.SidePick)
        {
            p!.PrintToChat("[Knife] Side choice is not currently available.");
            return;
        }
        if (!_knifeWinners.Contains(p!.SteamID))
        {
            p.PrintToChat("[Knife] Only a member of the knife-winning team may choose sides.");
            return;
        }
        StartLive(swap);
    }

    private void StartLive(bool swap)
    {
        if (_phase != MatchPhase.SidePick) return;
        bool winnersAreCT = (_knifeWinnerTeam == (int)CsTeam.CounterTerrorist) != swap;
        _phase = MatchPhase.Live;
        ++_sideSerial;
        _knifeWinners.Clear();
        if (swap)
        {
            // Capture teams before moving anyone; SwitchTeam changes Team in-place.
            var players = TeamPlayers().Select(x => (Player: x, OriginalTeam: x.Team)).ToList();
            foreach (var (player, originalTeam) in players)
            {
                if (!player.IsValid) continue;
                try
                {
                    player.SwitchTeam(originalTeam == CsTeam.Terrorist
                        ? CsTeam.CounterTerrorist : CsTeam.Terrorist);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PugFlow] Team swap failed for {player.PlayerName}: {ex}");
                }
            }
        }
        Server.PrintToChatAll($"[Knife] {(swap ? "SWAP" : "STAY")}: knife winners play {(winnersAreCT ? "CT" : "T")}. Starting LIVE MR12 now!");
        Server.PrintToChatAll("[Match] 12:12 goes to MR3 overtime, repeated until a team wins. !help lists commands.");
        // live.cfg also resets round scores, removes warmup equipment rules,
        // restores economy and respawn rules, and restarts the match.
        Server.ExecuteCommand("exec live.cfg");
    }

    private void Status(CCSPlayerController? p, CommandInfo info)
    {
        if (!Human(p)) return;
        var count = _phase == MatchPhase.Voting ? PruneBallots() : Humans().Count;
        var details = _phase switch
        {
            MatchPhase.Warmup when OnRushMap() => $"Rush map, free warmup ({count} online). Type !votestart to start Rush.",
            MatchPhase.Live when OnRushMap() => "Rush match is LIVE.",
            MatchPhase.Warmup => $"Free warmup ({count} online). Type !votestart.",
            MatchPhase.Voting => $"Voting: {_yes.Count} YES, {_no.Count} NO. {NeededYes(count)} YES needed.",
            MatchPhase.KnifePending => "Knife round is starting.",
            MatchPhase.KnifeActive => "Knife round active.",
            MatchPhase.SidePick => "Knife winner must choose !ct / !t (or !stay / !swap).",
            MatchPhase.Live => "Competitive match is LIVE.",
            _ => "Unknown"
        };
        p!.PrintToChat("[PugFlow] " + details + " !help lists commands.");
    }

    private string PhaseName() => _phase switch
    {
        MatchPhase.Warmup when OnRushMap() => "Rush map, free warmup",
        MatchPhase.Live when OnRushMap() => "Live Rush match",
        MatchPhase.Warmup => "Free warmup",
        MatchPhase.Voting => "Start vote open",
        MatchPhase.KnifePending => "Knife round starting",
        MatchPhase.KnifeActive => "Knife round",
        MatchPhase.SidePick => "Knife winners picking a side",
        MatchPhase.Live => "Live match",
        _ => "Unknown"
    };

    private static bool HasAnyPermission(CCSPlayerController p, IEnumerable<string> permissions) =>
        permissions.Any(x => AdminManager.PlayerHasPermissions(p, x));

    private string? SidePickBlocker(CCSPlayerController p) =>
        _phase != MatchPhase.SidePick ? "after the knife round"
        : !_knifeWinners.Contains(p.SteamID) ? "knife winners only"
        : null;

    // Every chat command a player might try on this server, ours and RockTheVote's,
    // each with whether it works for this player in the current phase.
    private IEnumerable<PlayerCommand> PlayerCommands() => new PlayerCommand[]
    {
        new("!votestart", "start the match vote", _ =>
        {
            if (_phase != MatchPhase.Warmup) return "free warmup only";
            var wait = StartVoteCooldownSeconds - (DateTimeOffset.UtcNow - _lastVoteFailed).TotalSeconds;
            return wait > 0 ? $"wait {Math.Ceiling(wait)}s" : null;
        }),
        new("!yes / !no", "vote on the start vote", p =>
            _phase != MatchPhase.Voting ? "no vote open"
            : _yes.Contains(p.SteamID) || _no.Contains(p.SteamID) ? "already voted"
            : null),
        new("!ct / !t", "knife winners pick their side", SidePickBlocker),
        new("!stay / !swap", "knife winners keep or swap sides", SidePickBlocker),
        new(".drop", "drop knives for others to pick up", _ =>
            _phase is MatchPhase.KnifePending or MatchPhase.KnifeActive ? "not in the knife round" : null),
        new("!startstatus", "show the match phase", _ => null),
        new("!votemap <map>", "vote to change the map", _ => null),
        new("!rtv", "rock the vote for a new map", _ =>
            InWarmup() ? "not during warmup"
            : (DateTimeOffset.UtcNow - _mapStarted).TotalSeconds < RtvMapStartDelaySeconds ? "3 min after map start"
            : null),
        new("!nominate <map>", "nominate the next map", p =>
            HasAnyPermission(p, NominatePermissions) ? null : "VIP only"),
        new("!maps / !timeleft", "map list / time left", _ => null),
        new("!menutype", "change the vote menu style", _ => null),
        new("!extend", "vote to extend the map", _ => "disabled on this server"),
        new("!mapmenu", "admin: pick the next map", p =>
            AdminManager.PlayerHasPermissions(p, MapMenuPermission) ? null : "admin only"),
        new("!startreset", "admin: back to free warmup", p =>
            AdminManager.PlayerHasPermissions(p, ResetPermission) ? null : "admin only"),
    };

    private void Help(CCSPlayerController? p, CommandInfo info)
    {
        if (!Human(p)) return;
        var player = p!;
        // CS2 chat only applies colours when the message doesn't start with one, hence the leading space.
        player.PrintToChat($" {ChatColors.Gold}[Commands]{ChatColors.Default} {PhaseName()}. {ChatColors.Green}Green{ChatColors.Default} = works now, {ChatColors.Grey}grey{ChatColors.Default} = not now.");
        foreach (var command in PlayerCommands())
        {
            var reason = command.Unavailable(player);
            player.PrintToChat(reason == null
                ? $" {ChatColors.Green}{command.Name}{ChatColors.Default} {command.Description}"
                : $" {ChatColors.Grey}{command.Name} {command.Description} ({reason})");
        }
        player.PrintToChat($" {ChatColors.Grey}Not on this server: !ready !pause !unpause !tech !gg");
    }

    private void ForceReset(CCSPlayerController? p, CommandInfo info)
    {
        // p == null is the server console / RCON.
        if (p != null && !AdminManager.PlayerHasPermissions(p, ResetPermission))
        {
            info.ReplyToCommand($"[PugFlow] You need {ResetPermission} to use this.");
            return;
        }
        ReturnToWarmup("An admin reset the match. Free warmup restored.");
        info.ReplyToCommand("[PugFlow] Reset to free warmup.");
    }

    // ".drop" in chat: drop copies of the player's own knife at their feet for others to pick up.
    private HookResult OnChat(CCSPlayerController? p, CommandInfo info)
    {
        var text = info.ArgString.Trim().Trim('"').Trim();
        if (!Human(p) || !text.Equals(".drop", StringComparison.OrdinalIgnoreCase))
            return HookResult.Continue;
        var id = p!.SteamID;
        Server.NextFrame(() => DropKnives(id));
        return HookResult.Continue;
    }

    private static bool HasKnife(CCSPlayerPawn pawn)
    {
        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons == null) return false;
        foreach (var handle in weapons)
        {
            var name = handle.Value?.DesignerName;
            if (name != null && (name.Contains("knife") || name.Contains("bayonet"))) return true;
        }
        return false;
    }

    private void DropKnives(ulong id)
    {
        var p = Humans().FirstOrDefault(x => x.SteamID == id);
        if (p == null) return;
        if (_phase == MatchPhase.KnifePending || _phase == MatchPhase.KnifeActive)
        {
            p.PrintToChat("[Drop] Not available during the knife round.");
            return;
        }
        var pawn = p.PlayerPawn.Value;
        var origin = pawn?.AbsOrigin;
        if (pawn == null || origin == null || !p.PawnIsAlive
            || (p.Team != CsTeam.Terrorist && p.Team != CsTeam.CounterTerrorist))
        {
            p.PrintToChat("[Drop] You must be alive on a team to use .drop.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_phase == MatchPhase.Live)
        {
            if (_dropRound.TryGetValue(id, out var round) && round == _roundSerial)
            {
                p.PrintToChat("[Drop] .drop can be used once per round.");
                return;
            }
        }
        else if (_dropTime.TryGetValue(id, out var last))
        {
            var remaining = DropCooldownSeconds - (now - last).TotalSeconds;
            if (remaining > 0)
            {
                p.PrintToChat($"[Drop] Wait {Math.Ceiling(remaining)}s before using .drop again.");
                return;
            }
        }

        // The player keeps their own knife, so each extra copy can't be picked up and stays on the ground.
        if (!HasKnife(pawn)) p.GiveNamedItem("weapon_knife");

        float x = origin.X, y = origin.Y, z = origin.Z;
        int dropped = 0;
        for (int i = 0; i < DropKnifeCount; i++)
        {
            try
            {
                var knife = p.GiveNamedItem<CBasePlayerWeapon>("weapon_knife");
                if (knife == null || !knife.IsValid || knife.OwnerEntity.IsValid) continue;
                // Small ring around the feet so the copies don't spawn inside each other; zero velocity = straight down.
                double angle = 2 * Math.PI * i / DropKnifeCount;
                knife.Teleport(
                    new Vector(x + (float)(Math.Cos(angle) * 12), y + (float)(Math.Sin(angle) * 12), z + 32),
                    new QAngle(0, 0, 0),
                    new Vector(0, 0, 0));
                ++dropped;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PugFlow] Knife drop failed for {id}: {ex}");
            }
        }
        if (dropped == 0)
        {
            p.PrintToChat("[Drop] Could not drop knives right now.");
            return;
        }

        _dropRound[id] = _roundSerial;
        _dropTime[id] = now;
        Server.PrintToChatAll($"[Drop] {p.PlayerName} dropped {dropped} knives. Press E on one to pick it up (or drop yours with G first).");
    }

    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect e, GameEventInfo info)
    {
        var leaving = e.Userid;
        if (!Human(leaving)) return HookResult.Continue;

        // Checked synchronously: an empty server may hibernate and not run timers.
        if (_phase == MatchPhase.Voting)
        {
            EvaluateVote(PruneBallots(leaving));
        }
        else if (_phase != MatchPhase.Warmup && Humans(leaving).Count == 0)
        {
            ReturnToWarmup("All players left. Free warmup restored.");
        }
        return HookResult.Continue;
    }

    private void OnMapStart(string mapName)
    {
        ResetForMap();
        _mapStarted = DateTimeOffset.UtcNow;
        // Runs after the gamemode cfg, so Rush maps also get free warmup; rush_live.cfg
        // restores Rush rules when the match starts.
        AddTimer(3f, () =>
        {
            if (_phase != MatchPhase.Warmup) return;
            Server.ExecuteCommand("exec practice.cfg");
            Server.PrintToChatAll(OnRushMap()
                ? "[PugFlow] Rush map, free warmup. !votestart -> live Rush (no knife round). !help lists commands."
                : "[PugFlow] Free warmup. !votestart -> knife round -> side pick -> live. !help lists commands.");
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void OnMapEnd() => ResetForMap();

    private void ResetForMap()
    {
        _phase = MatchPhase.Warmup;
        ClearBallots();
        ++_sideSerial;
        _knifeWinners.Clear();
        _knifeWinnerTeam = 0;
        _lastVoteFailed = DateTimeOffset.MinValue;
        _dropRound.Clear();
        _dropTime.Clear();
    }

    private void ReturnToWarmup(string message)
    {
        ResetForMap();
        Server.ExecuteCommand("exec practice.cfg");
        Server.PrintToChatAll($"[PugFlow] {message}");
    }

    [GameEventHandler]
    public HookResult OnMatchWinPanel(EventCsWinPanelMatch e, GameEventInfo info)
    {
        // Running practice.cfg can't leave the game-over screen, and without a
        // map cycle CS2 stays there; reload the map so OnMapStart restores warmup.
        // The timer dies on map change, so a map picked by RockTheVote wins.
        var map = Server.MapName;
        Server.PrintToChatAll($"[PugFlow] Match over. Reloading the map in {MatchEndReloadSeconds:0}s for free warmup.");
        AddTimer(MatchEndReloadSeconds, () =>
        {
            if (Server.MapName != map) return;
            ReloadMap(map);
        }, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    private void RefreshStaleMap()
    {
        if (Server.CurrentTime < StaleMapSeconds || Humans().Count > 0) return;
        var map = Server.MapName;
        Console.WriteLine($"[PugFlow] Server empty and {map} has run {Server.CurrentTime / 3600:0.0}h: reloading it to reset game time.");
        ReloadMap(map);
    }

    private void ReloadMap(string map)
    {
        if (Server.IsMapValid(map))
        {
            Server.ExecuteCommand($"changelevel {map}");
            return;
        }
        // Workshop maps aren't valid changelevel targets. Maps listed with their ID in
        // RockTheVote's map list load by ID; ds_workshop_changelevel only works for maps
        // in a hosted workshop collection, which this server doesn't use.
        var workshopId = WorkshopIdFromRtvMapList(map);
        Server.ExecuteCommand(workshopId != null
            ? $"host_workshop_map {workshopId}"
            : $"ds_workshop_changelevel {map}");
    }

    // RockTheVote's maplist.txt lists workshop maps as "name:workshopId"; the name is the
    // map's internal name (what Server.MapName reports), e.g. "fy_snow_legacy:3592238209".
    private string? WorkshopIdFromRtvMapList(string map)
    {
        var path = Path.Combine(ModuleDirectory, "..", "RockTheVote", "maplist.txt");
        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//")) continue;
                var colon = line.LastIndexOf(':');
                if (colon <= 0) continue;
                var id = line[(colon + 1)..].Trim();
                if (line[..colon].Trim().Equals(map, StringComparison.OrdinalIgnoreCase) && id.All(char.IsDigit) && id.Length > 0)
                    return id;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PugFlow] Could not read {path}: {ex.Message}");
        }
        return null;
    }
}
