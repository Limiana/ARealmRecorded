using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dalamud.Hooking;
using Dalamud.Logging;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ARealmRecorded;

public unsafe class Game
{
    private const int replayHeaderSize = 0x364;
    private static readonly string replayFolder = Path.Combine(Framework.Instance()->UserPath, "replay");
    private static readonly string autoRenamedFolder = Path.Combine(replayFolder, "autorenamed");
    private static readonly string deletedFolder = Path.Combine(replayFolder, "deleted");
    private static bool replayLoaded;
    private static IntPtr replayBytesPtr;
    public static string lastSelectedReplay;
    private static Structures.FFXIVReplay.Header lastSelectedHeader;

    public static bool quickLoadEnabled = true;
    private static int quickLoadChapter = -1;
    private static int seekingChapter = 0;
    private static uint seekingOffset = 0;

    private static int currentRecordingSlot = -1;
    private static readonly Regex bannedFolderCharacters = new("[\\\\\\/:\\*\\?\"\\<\\>\\|]");

    private static readonly HashSet<uint> whitelistedContentTypes = new() { 1, 2, 3, 4, 5, 9, 28 }; // 22 Event, 26 Eureka, 27 Carnivale, 29 Bozja

    private static List<(FileInfo, Structures.FFXIVReplay.Header)> replayList;
    public static List<(FileInfo, Structures.FFXIVReplay.Header)> ReplayList => replayList ?? GetReplayList();

    private static readonly Memory.Replacer alwaysRecordReplacer = new("24 06 3C 02 75 08 48 8B CB E8", new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, true);
    private static readonly Memory.Replacer removeRecordReadyToastReplacer = new("BA CB 07 00 00 48 8B CF E8", new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 }, true);
    private static readonly Memory.Replacer removeProcessingLimitReplacer = new("41 FF C6 E8 ?? ?? ?? ?? 48 8B F8 48 85 C0 0F 84", new byte[] { 0x90, 0x90, 0x90 }, true);
    private static readonly Memory.Replacer removeProcessingLimitReplacer2 = new("77 57 48 8B 0D ?? ?? ?? ?? 33 C0", new byte[] { 0x90, 0x90 }, true);
    private static readonly Memory.Replacer forceFastForwardReplacer = new("0F 83 ?? ?? ?? ?? 0F B7 47 02 4C 8D 47 0C", new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 });

    [Signature("48 8D 0D ?? ?? ?? ?? 88 44 24 24", ScanType = ScanType.StaticAddress)]
    public static Structures.FFXIVReplay* ffxivReplay;

    [Signature("48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? EB 0E", ScanType = ScanType.StaticAddress)]
    private static byte* waymarkToggle; // Actually a uint, but only seems to use the first 2 bits

    public static bool InPlayback => (ffxivReplay->playbackControls & 4) != 0;
    public static bool IsRecording => (ffxivReplay->status & 0x74) == 0x74;

    public static bool IsWaymarkVisible => (*waymarkToggle & 2) == 0;

    [Signature("?? ?? 00 00 01 75 74 85 FF 75 07 E8")]
    public static short contentDirectorOffset;

    [Signature("40 53 48 83 EC 20 0F B6 81 ?? ?? ?? ?? 48 8B D9 A8 04 74 5D")]
    private static delegate* unmanaged<Structures.FFXIVReplay*, byte, void> beginRecording;
    public static void BeginRecording() => beginRecording(ffxivReplay, 1);

    [Signature("E8 ?? ?? ?? ?? 84 C0 74 8D")]
    private static delegate* unmanaged<Structures.FFXIVReplay*, byte, byte> setChapter;
    private static byte SetChapter(byte chapter) => setChapter(ffxivReplay, chapter);

    //[Signature("E9 ?? ?? ?? ?? 48 83 4B 70 04")]
    //private static delegate* unmanaged<Structures.FFXIVReplay*, byte, byte> addRecordingChapter;
    //public static bool AddRecordingChapter(byte type) => addRecordingChapter(ffxivReplay, type) != 0;

    //[Signature("40 53 48 83 EC 20 0F B6 81 ?? ?? ?? ?? 48 8B D9 24 06 3C 04 75 5D 83 B9")]
    //private static delegate* unmanaged<Structures.FFXIVReplay*, void> resetPlayback;
    //public static void ResetPlayback() => resetPlayback(ffxivReplay);

    [Signature("48 89 5C 24 10 57 48 81 EC 70 04 00 00")]
    private static delegate* unmanaged<IntPtr, void> displaySelectedDutyRecording;
    public static void DisplaySelectedDutyRecording(IntPtr agent) => displaySelectedDutyRecording(agent);

    private delegate void InitializeRecordingDelegate(Structures.FFXIVReplay* ffxivReplay);
    [Signature("40 55 57 48 8D 6C 24 B1 48 81 EC 98 00 00 00", DetourName = "InitializeRecordingDetour")]
    private static Hook<InitializeRecordingDelegate> InitializeRecordingHook;
    private static void InitializeRecordingDetour(Structures.FFXIVReplay* ffxivReplay)
    {
        var id = ffxivReplay->initZonePacket.contentFinderCondition;
        if (id == 0) return;

        var contentFinderCondition = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.GeneratedSheets.ContentFinderCondition>()?.GetRow(id);
        if (contentFinderCondition == null) return;

        var contentType = contentFinderCondition.ContentType.Row;
        if (!whitelistedContentTypes.Contains(contentType)) return;

        FixNextReplaySaveSlot();
        InitializeRecordingHook.Original(ffxivReplay);
        BeginRecording();

        if (contentDirectorOffset > 0)
            ContentDirectorTimerUpdateHook?.Enable();
    }

    private delegate byte RequestPlaybackDelegate(Structures.FFXIVReplay* ffxivReplay, byte slot);
    [Signature("48 89 5C 24 08 57 48 83 EC 30 F6 81 ?? ?? ?? ?? 04", DetourName = "RequestPlaybackDetour")] // E8 ?? ?? ?? ?? EB 2B 48 8B CB 89 53 2C (+0x14)
    private static Hook<RequestPlaybackDelegate> RequestPlaybackHook;
    public static byte RequestPlaybackDetour(Structures.FFXIVReplay* ffxivReplay, byte slot)
    {
        var customSlot = slot == 100;
        Structures.FFXIVReplay.Header prevHeader = new();

        if (customSlot)
        {
            slot = 0;
            prevHeader = ffxivReplay->savedReplayHeaders[0];
            ffxivReplay->savedReplayHeaders[0] = lastSelectedHeader;
        }
        else
        {
            lastSelectedReplay = null;
        }

        var ret = RequestPlaybackHook.Original(ffxivReplay, slot);

        if (customSlot)
            ffxivReplay->savedReplayHeaders[0] = prevHeader;

        return ret;
    }

    private delegate void BeginPlaybackDelegate(Structures.FFXIVReplay* ffxivReplay, byte canEnter);
    [Signature("E8 ?? ?? ?? ?? 0F B7 17 48 8B CB", DetourName = "BeginPlaybackDetour")]
    private static Hook<BeginPlaybackDelegate> BeginPlaybackHook;
    private static void BeginPlaybackDetour(Structures.FFXIVReplay* ffxivReplay, byte allowed)
    {
        BeginPlaybackHook.Original(ffxivReplay, allowed);
        if (allowed == 0) return;

        if (string.IsNullOrEmpty(lastSelectedReplay))
            LoadReplay(ffxivReplay->currentReplaySlot);
        else
            LoadReplay(lastSelectedReplay);
    }

    [Signature("E8 ?? ?? ?? ?? F6 83 ?? ?? ?? ?? 04 74 38 F6 83 ?? ?? ?? ?? 01", DetourName = "PlaybackUpdateDetour")]
    private static Hook<InitializeRecordingDelegate> PlaybackUpdateHook;
    private static void PlaybackUpdateDetour(Structures.FFXIVReplay* ffxivReplay)
    {
        PlaybackUpdateHook.Original(ffxivReplay);

        UpdateAutoRename();

        if (IsRecording && ffxivReplay->chapters[0]->type == 1) // For some reason the barrier dropping in dungeons is 5, but in trials it's 1
            ffxivReplay->chapters[0]->type = 5;

        if (!replayLoaded || !InPlayback) return;

        ffxivReplay->dataLoadType = 0;
        ffxivReplay->dataOffset = 0;

        if (quickLoadChapter < 2) return;

        var seekedTime = ffxivReplay->chapters[seekingChapter]->ms / 1000f;
        if (seekedTime > ffxivReplay->seek) return;

        DoQuickLoad();
    }

    private delegate Structures.FFXIVReplay.ReplayDataSegment* GetReplayDataSegmentDelegate(Structures.FFXIVReplay* ffxivReplay);
    [Signature("40 53 48 83 EC 20 8B 81 90 00 00 00")]
    private static Hook<GetReplayDataSegmentDelegate> GetReplayDataSegmentHook;
    public static Structures.FFXIVReplay.ReplayDataSegment* GetReplayDataSegmentDetour(Structures.FFXIVReplay* ffxivReplay)
    {
        // Needs to be here to prevent infinite looping
        if (seekingOffset > 0 && seekingOffset <= ffxivReplay->overallDataOffset)
        {
            forceFastForwardReplacer.Disable();
            seekingOffset = 0;
        }

        // Absurdly hacky, but it works
        if (!quickLoadEnabled || ffxivReplay->seekDelta >= 400)
            removeProcessingLimitReplacer2.Disable();
        else
            removeProcessingLimitReplacer2.Enable();

        if (!replayLoaded)
            return GetReplayDataSegmentHook.Original(ffxivReplay);
        if (ffxivReplay->overallDataOffset >= ffxivReplay->replayHeader.replayLength)
            return null;
        return (Structures.FFXIVReplay.ReplayDataSegment*)((long)replayBytesPtr + replayHeaderSize + ffxivReplay->overallDataOffset);
    }

    private delegate void OnSetChapterDelegate(Structures.FFXIVReplay* ffxivReplay, byte chapter);
    [Signature("48 89 5C 24 08 57 48 83 EC 30 48 8B D9 0F B6 FA 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 85 C0 74 24", DetourName = "OnSetChapterDetour")]
    private static Hook<OnSetChapterDelegate> OnSetChapterHook;
    private static void OnSetChapterDetour(Structures.FFXIVReplay* ffxivReplay, byte chapter)
    {
        OnSetChapterHook.Original(ffxivReplay, chapter);

        if (!quickLoadEnabled || chapter <= 0 || ffxivReplay->chapters.length < 2) return;

        quickLoadChapter = chapter;
        seekingChapter = -1;
        DoQuickLoad();
    }

    private delegate byte ExecuteCommandDelegate(int a1, int a2, int a3, int a4, int a5);
    [Signature("E8 ?? ?? ?? ?? 8D 43 0A")]
    private static Hook<ExecuteCommandDelegate> ExecuteCommandHook;
    private static byte ExecuteCommandDetour(int a1, int a2, int a3, int a4, int a5) => (byte)(!InPlayback || a1 == 1981 ? ExecuteCommandHook.Original(a1, a2, a3, a4, a5) : 0); // Block GPose and Idle Camera from sending packets

    private delegate byte DisplayRecordingOnDTRBarDelegate(IntPtr agent);
    [Signature("E8 ?? ?? ?? ?? 44 0F B6 C0 BA 4F 00 00 00")]
    private static Hook<DisplayRecordingOnDTRBarDelegate> DisplayRecordingOnDTRBarHook;
    private static byte DisplayRecordingOnDTRBarDetour(IntPtr agent) => (byte)(DisplayRecordingOnDTRBarHook.Original(agent) != 0
        || ARealmRecorded.Config.EnableRecordingIcon && IsRecording && DalamudApi.PluginInterface.UiBuilder.ShouldModifyUi ? 1 : 0);

    private delegate void ContentDirectorTimerUpdateDelegate(IntPtr contentDirector);
    [Signature("40 53 48 83 EC 20 0F B6 81 ?? ?? ?? ?? 48 8B D9 A8 04 0F 84 ?? ?? ?? ?? A8 08", DetourName = "ContentDirectorTimerUpdateDetour")]
    private static Hook<ContentDirectorTimerUpdateDelegate> ContentDirectorTimerUpdateHook;
    private static void ContentDirectorTimerUpdateDetour(IntPtr contentDirector)
    {
        if ((*(byte*)(contentDirector + contentDirectorOffset) & 12) == 12)
        {
            ffxivReplay->status |= 64;
            ContentDirectorTimerUpdateHook.Disable();
        }

        ContentDirectorTimerUpdateHook.Original(contentDirector);
    }

    private delegate IntPtr EventBeginDelegate(IntPtr a1, IntPtr a2);
    [Signature("40 55 53 57 41 55 41 57 48 8D 6C 24 C9")]
    private static Hook<EventBeginDelegate> EventBeginHook;
    private static IntPtr EventBeginDetour(IntPtr a1, IntPtr a2) => !InPlayback ? EventBeginHook.Original(a1, a2) : IntPtr.Zero;

    public static string GetReplaySlotName(int slot) => $"FFXIV_{DalamudApi.ClientState.LocalContentId:X16}_{slot:D3}.dat";

    private static void UpdateAutoRename()
    {
        switch (IsRecording)
        {
            case true when currentRecordingSlot < 0:
                currentRecordingSlot = ffxivReplay->nextReplaySaveSlot;
                break;
            case false when currentRecordingSlot >= 0:
                AutoRenameRecording();
                currentRecordingSlot = -1;
                break;
        }
    }

    public static void LoadReplay(int slot) => LoadReplay(Path.Combine(replayFolder, GetReplaySlotName(slot)));

    public static void LoadReplay(string path)
    {
        var newReplay = ReadReplay(path);
        if (newReplay == IntPtr.Zero) return;

        if (replayLoaded)
            Marshal.FreeHGlobal(replayBytesPtr);

        replayBytesPtr = newReplay;
        replayLoaded = true;
        LoadReplayInfo();
        ffxivReplay->dataLoadType = 0;

        ARealmRecorded.Config.LastLoadedReplay = path;
    }

    public static IntPtr ReadReplay(string path)
    {
        var ptr = IntPtr.Zero;
        var allocated = false;

        try
        {
            using var fs = File.OpenRead(path);

            ptr = Marshal.AllocHGlobal((int)fs.Length);
            allocated = true;

            _ = fs.Read(new Span<byte>((void*)ptr, (int)fs.Length));
        }
        catch (Exception e)
        {
            PluginLog.Error($"Failed to read replay {path}\n{e}");

            if (allocated)
            {
                Marshal.FreeHGlobal(ptr);
                ptr = IntPtr.Zero;
            }
        }

        return ptr;
    }

    public static Structures.FFXIVReplay.Header? ReadReplayHeader(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var bytes = new byte[replayHeaderSize];
            if (fs.Read(bytes, 0, replayHeaderSize) != replayHeaderSize)
                return null;
            fixed (byte* ptr = &bytes[0])
                return *(Structures.FFXIVReplay.Header*)ptr;
        }
        catch (Exception e)
        {
            PluginLog.Error($"Failed to read replay header {path}\n{e}");
            return null;
        }
    }

    public static void LoadReplayInfo()
    {
        if (!replayLoaded) return;
        ffxivReplay->replayHeader = *(Structures.FFXIVReplay.Header*)replayBytesPtr;
        ffxivReplay->chapters = *(Structures.FFXIVReplay.ChapterArray*)(replayBytesPtr + sizeof(Structures.FFXIVReplay.Header));
    }

    public static void FixNextReplaySaveSlot()
    {
        for (byte i = 0; i < 3; i++)
        {
            if (i != 2)
            {
                var header = ffxivReplay->savedReplayHeaders[i];
                if (header.IsLocked) continue;
            }

            ffxivReplay->nextReplaySaveSlot = i;
            return;
        }
    }

    public static byte FindNextChapterType(byte startChapter, byte type)
    {
        for (byte i = (byte)(startChapter + 1); i < ffxivReplay->chapters.length; i++)
            if (ffxivReplay->chapters[i]->type == type) return i;
        return 0;
    }

    public static byte GetPreviousStartChapter(byte chapter)
    {
        var foundPreviousStart = false;
        for (byte i = chapter; i > 0; i--)
        {
            if (ffxivReplay->chapters[i]->type != 2) continue;

            if (foundPreviousStart)
                return i;
            foundPreviousStart = true;
        }
        return 0;
    }

    public static byte FindPreviousChapterFromTime(uint ms)
    {
        for (byte i = (byte)(ffxivReplay->chapters.length - 1); i > 0; i--)
            if (ffxivReplay->chapters[i]->ms <= ms) return i;
        return 0;
    }

    public static Structures.FFXIVReplay.ReplayDataSegment* FindNextDataSegment(uint ms, out uint offset)
    {
        offset = 0;

        while (ffxivReplay->replayHeader.replayLength > offset)
        {
            var segment = (Structures.FFXIVReplay.ReplayDataSegment*)((long)replayBytesPtr + replayHeaderSize + offset);
            if (segment->ms >= ms) return segment;
            offset += (uint)(0xC + segment->dataLength);
        }

        return null;
    }

    public static void JumpToChapter(byte chapter)
    {
        var jumpChapter = ffxivReplay->chapters[chapter];
        if (jumpChapter == null) return;
        ffxivReplay->overallDataOffset = jumpChapter->offset;
        ffxivReplay->seek = jumpChapter->ms / 1000f;
    }

    public static void JumpToTime(uint ms)
    {
        var segment = FindNextDataSegment(ms, out var offset);
        if (segment == null) return;
        ffxivReplay->overallDataOffset = offset;
        ffxivReplay->seek = segment->ms / 1000f;
    }

    public static void JumpToTimeBeforeChapter(byte chapter, uint ms)
    {
        var jumpChapter = ffxivReplay->chapters[chapter];
        if (jumpChapter == null) return;
        JumpToTime(jumpChapter->ms > ms ? jumpChapter->ms - ms : 0);
    }

    public static void SeekToTime(uint ms)
    {
        if (ffxivReplay->selectedChapter != 64) return;

        var prevChapter = FindPreviousChapterFromTime(ms);
        var segment = FindNextDataSegment(ms, out var offset);
        if (segment == null) return;

        seekingOffset = offset;
        forceFastForwardReplacer.Enable();
        SetChapter(prevChapter);
    }

    public static void ReplaySection(byte from, byte to)
    {
        if (from != 0 && ffxivReplay->overallDataOffset < ffxivReplay->chapters[from]->offset)
            JumpToChapter(from);

        seekingChapter = to;
        if (seekingChapter >= quickLoadChapter)
            quickLoadChapter = -1;
    }

    public static void DoQuickLoad()
    {
        if (seekingChapter < 0)
        {
            ReplaySection(0, 1);
            return;
        }

        var nextEvent = FindNextChapterType((byte)seekingChapter, 4);
        if (nextEvent != 0 && nextEvent < quickLoadChapter - 1)
        {
            var nextCountdown = FindNextChapterType(nextEvent, 1);
            if (nextCountdown == 0 || nextCountdown > nextEvent + 2)
                nextCountdown = (byte)(nextEvent + 1);
            ReplaySection(nextEvent, nextCountdown);
            return;
        }

        ReplaySection(GetPreviousStartChapter((byte)quickLoadChapter), (byte)quickLoadChapter);
    }

    public static bool EnterGroupPose()
    {
        var uiModule = Framework.Instance()->GetUiModule();
        return ((delegate* unmanaged<UIModule*, byte>)uiModule->vfunc[74])(uiModule) != 0; // 48 89 5C 24 08 57 48 83 EC 20 48 8B F9 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B C8
    }

    public static bool EnterIdleCamera()
    {
        var uiModule = Framework.Instance()->GetUiModule();
        var focus = DalamudApi.TargetManager.FocusTarget;
        return ((delegate* unmanaged<UIModule*, byte, long, byte>)uiModule->vfunc[77])(uiModule, 0, focus != null ? focus.ObjectId : 0xE0000000) != 0; // 48 89 5C 24 08 57 48 83 EC 20 48 8B 01 49 8B D8 0F B6 FA
    }

    public static List<(FileInfo, Structures.FFXIVReplay.Header)> GetReplayList()
    {
        try
        {
            var directory = new DirectoryInfo(replayFolder);

            var renamedDirectory = new DirectoryInfo(autoRenamedFolder);
            if (!renamedDirectory.Exists)
                renamedDirectory.Create();

            var list = (from file in directory.GetFiles().Concat(renamedDirectory.GetFiles())
                    where file.Extension == ".dat"
                    let header = ReadReplayHeader(file.FullName)
                    where header is { IsValid: true }
                    select (file, header.Value)
                ).OrderByDescending(t => t.Value.IsPlayable).ThenBy(t => t.file.Name).ToList();

            replayList = list;
        }
        catch
        {
            replayList = new();
        }

        return replayList;
    }

    public static void RenameRecording(FileInfo file, string name)
    {
        try
        {
            file.MoveTo(Path.Combine(replayFolder, $"{name}.dat"));
        }
        catch (Exception e)
        {
            ARealmRecorded.PrintError($"Failed to rename recording\n{e}");
        }
    }

    public static void AutoRenameRecording()
    {
        try
        {
            var fileName = GetReplaySlotName(currentRecordingSlot);
            var (file, _) = GetReplayList().First(t => t.Item1.Name == fileName);

            var name = $"{bannedFolderCharacters.Replace(ffxivReplay->contentTitle.ToString(), "")} {DateTime.Now:yyyy.MM.dd HH.mm.ss}";
            file.MoveTo(Path.Combine(autoRenamedFolder, $"{name}.dat"));

            var renamedFiles = new DirectoryInfo(autoRenamedFolder).GetFiles().Where(f => f.Extension == ".dat").ToList();
            if (renamedFiles.Count > 30)
                renamedFiles.OrderBy(f => f.CreationTime).First().Delete();

            GetReplayList();
            ffxivReplay->savedReplayHeaders[currentRecordingSlot] = new Structures.FFXIVReplay.Header();
        }
        catch (Exception e)
        {
            ARealmRecorded.PrintError($"Failed to rename recording\n{e}");
        }
    }

    public static void DeleteRecording(FileInfo file)
    {
        try
        {
            var deletedDirectory = new DirectoryInfo(deletedFolder);
            if (!deletedDirectory.Exists)
                deletedDirectory.Create();

            file.MoveTo(Path.Combine(deletedFolder, file.Name));

            var deletedFiles = deletedDirectory.GetFiles().Where(f => f.Extension == ".dat").ToList();
            if (deletedFiles.Count > 10)
                deletedFiles.OrderBy(f => f.CreationTime).First().Delete();

            GetReplayList();
            ARealmRecorded.PrintEcho("Successfully moved the recording to the deleted folder!");
        }
        catch (Exception e)
        {
            ARealmRecorded.PrintError($"Failed to delete recording\n{e}");
        }
    }

    public static void SetDutyRecorderMenuSelection(IntPtr agent, byte slot)
    {
        *(byte*)(agent + 0x2C) = slot;
        *(byte*)(agent + 0x2A) = 1;
        DisplaySelectedDutyRecording(agent);
    }

    public static void SetDutyRecorderMenuSelection(IntPtr agent, string path, Structures.FFXIVReplay.Header header)
    {
        //header.localCID = DalamudApi.ClientState.LocalContentId; // TODO: Fix bug
        lastSelectedReplay = path;
        lastSelectedHeader = header;
        var prevHeader = ffxivReplay->savedReplayHeaders[0];
        ffxivReplay->savedReplayHeaders[0] = header;
        SetDutyRecorderMenuSelection(agent, 0);
        ffxivReplay->savedReplayHeaders[0] = prevHeader;
        *(byte*)(agent + 0x2C) = 100;
    }

    public static void CopyRecordingIntoSlot(IntPtr agent, FileInfo file, Structures.FFXIVReplay.Header header, byte slot)
    {
        if (slot > 2) return;
        try
        {
            file.CopyTo(Path.Combine(file.DirectoryName!, GetReplaySlotName(slot)), true);
            ffxivReplay->savedReplayHeaders[slot] = header;
            SetDutyRecorderMenuSelection(agent, slot);
            GetReplayList();
        }
        catch (Exception e)
        {
            ARealmRecorded.PrintError($"Failed to copy recording to slot {slot + 1}\n{e}");
        }
    }

    public static void OpenReplayFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = replayFolder,
                UseShellExecute = true
            });
        }
        catch { }
    }

    public static void ToggleWaymarks()
    {
        if ((*waymarkToggle & 2) != 0)
            *waymarkToggle -= 2;
        else
            *waymarkToggle += 2;
    }

#if DEBUG
    public static void ReadPackets(string path)
    {
        var ptr = ReadReplay(path);
        if (ptr == IntPtr.Zero) return;

        var header = (Structures.FFXIVReplay.Header*)ptr;
        var opcodeCount = new Dictionary<uint, uint>();
        var opcodeLengths = new Dictionary<uint, uint>();

        var offset = 0;
        var totalPackets = 0;
        while (header->replayLength > offset)
        {
            var segment = (Structures.FFXIVReplay.ReplayDataSegment*)((long)ptr + replayHeaderSize + offset);

            opcodeCount.TryGetValue(segment->opcode, out var count);
            opcodeCount[segment->opcode] = ++count;

            opcodeLengths[segment->opcode] = segment->dataLength;
            offset += 0xC + segment->dataLength;
            ++totalPackets;
        }

        PluginLog.Information("-------------------");
        PluginLog.Information($"Opcodes inside: {path} (Total: [{opcodeCount.Count}] {totalPackets})");
        foreach (var (opcode, count) in opcodeCount)
            PluginLog.Information($"[{opcode:X}] {count} ({opcodeLengths[opcode]})");
        PluginLog.Information("-------------------");
    }
#endif

    // 48 89 5C 24 08 57 48 83 EC 20 33 FF 48 8B D9 89 39 48 89 79 08 ctor
    // E8 ?? ?? ?? ?? 48 8D 8B 48 0B 00 00 E8 ?? ?? ?? ?? 48 8D 8B 38 0B 00 00 dtor
    // 40 53 48 83 EC 20 80 A1 ?? ?? ?? ?? F3 Initialize
    // 40 53 48 83 EC 20 0F B6 81 ?? ?? ?? ?? 48 8B D9 A8 04 75 09 Update
    // 48 83 EC 38 0F B6 91 ?? ?? ?? ?? 0F B6 C2 RequestEndPlayback
    // E8 ?? ?? ?? ?? EB 10 41 83 78 04 00 EndPlayback
    // 48 89 5C 24 10 55 48 8B EC 48 81 EC 80 00 00 00 48 8B 05 Something to do with loading
    // E8 ?? ?? ?? ?? 3C 40 73 4A GetCurrentChapter
    // F6 81 ?? ?? ?? ?? 04 74 11 SetTimescale (No longer used by anything)
    // 40 53 48 83 EC 20 F3 0F 10 81 ?? ?? ?? ?? 48 8B D9 F3 0F 10 0D SetSoundTimescale1? Doesn't seem to work (Last function)
    // E8 ?? ?? ?? ?? 44 0F B6 D8 C7 03 02 00 00 00 Function handling the UI buttons

    public static void Initialize()
    {
        // TODO change back to static whenever support is added
        //SignatureHelper.Initialise(typeof(Game));
        SignatureHelper.Initialise(new Game());
        InitializeRecordingHook.Enable();
        PlaybackUpdateHook.Enable();
        RequestPlaybackHook.Enable();
        BeginPlaybackHook.Enable();
        GetReplayDataSegmentHook.Enable();
        OnSetChapterHook.Enable();
        ExecuteCommandHook.Enable();
        DisplayRecordingOnDTRBarHook.Enable();
        EventBeginHook.Enable();

        waymarkToggle += 0x48;

        if (InPlayback && ffxivReplay->fileStream != IntPtr.Zero && *(long*)ffxivReplay->fileStream == 0)
            LoadReplay(ARealmRecorded.Config.LastLoadedReplay);
    }

    public static void Dispose()
    {
        InitializeRecordingHook?.Dispose();
        PlaybackUpdateHook?.Dispose();
        RequestPlaybackHook?.Dispose();
        BeginPlaybackHook?.Dispose();
        GetReplayDataSegmentHook?.Dispose();
        OnSetChapterHook?.Dispose();
        ExecuteCommandHook?.Dispose();
        DisplayRecordingOnDTRBarHook?.Dispose();
        ContentDirectorTimerUpdateHook?.Dispose();
        EventBeginHook?.Dispose();

        if (!replayLoaded) return;

        if (InPlayback)
        {
            ffxivReplay->playbackControls |= 8; // Pause
            ARealmRecorded.PrintError("Plugin was unloaded, playback will be broken if the plugin or recording is not reloaded.");
        }

        Marshal.FreeHGlobal(replayBytesPtr);
        replayLoaded = false;
    }
}