using System.Buffers.Binary;
using System.Text;

namespace FireAxe;

// Only the Source layouts exercised by our local MDL49/53 prototypes are edited.
internal sealed class StudioReloadModel(byte[] data)
{
    internal sealed record Clip(int Sequence, string Name, int Animation, int Frames, double Fps, double[] EventFrames, string? Unsupported, WeaponAnimationKind Kind);
    internal sealed record Animation(int Offset, int Frames, float Fps, int SectionOffset, int SectionSize);
    private readonly byte[] _data = data;

    private void Range(int offset, long size)
    {
        if (offset < 0 || size < 0 || (long)offset + size > _data.Length) throw new InvalidDataException("模型数据超出文件范围。");
    }
    private int I(int offset) { Range(offset, 4); return BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(offset, 4)); }
    private short S(int offset) { Range(offset, 2); return BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(offset, 2)); }
    private float F(int offset) { Range(offset, 4); return BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(offset, 4)); }
    private string Text(int offset)
    {
        Range(offset, 1);
        var end = Array.IndexOf(_data, (byte)0, offset);
        if (end < 0) throw new InvalidDataException("模型字符串缺少结束标记。");
        return Encoding.UTF8.GetString(_data, offset, end - offset);
    }
    private int Table(int countOffset, int tableOffset, int stride)
    {
        var count = I(countOffset);
        Range(I(tableOffset), (long)count * stride);
        return count;
    }
    internal Animation Anim(int index)
    {
        var count = Table(180, 184, 100);
        if ((uint)index >= count) throw new InvalidDataException("动画编号无效。");
        var offset = checked(I(184) + index * 100);
        var frames = I(offset + 16);
        var fps = F(offset + 8);
        if (frames < 0 || !float.IsFinite(fps) || fps < 0) throw new InvalidDataException("动画帧数或 FPS 无效。");
        return new(offset, frames, fps, I(offset + 80), I(offset + 84));
    }
    internal List<Clip> Inspect()
    {
        Range(0, 196);
        if (!_data.AsSpan(0, 4).SequenceEqual("IDST"u8) || I(4) is not (49 or 53))
            throw new NotSupportedException("目前支持 MDL 49 / 53 模型。");
        if (I(76) != _data.Length) throw new InvalidDataException("模型声明长度与文件不符。");
        if (I(156) is < 1 or > 256) throw new NotSupportedException("此骨骼布局暂不支持。");
        var count = Table(188, 192, 212);
        var clips = new List<Clip>();
        var sequences = new List<(int Index, int Offset, string Label, string Activity, WeaponAnimationKind? Kind, short[] Animations)>();
        for (var index = 0; index < count; index++)
        {
            var offset = checked(I(192) + index * 212);
            var label = Text(offset + I(offset + 4));
            var activity = I(offset + 8) == 0 ? "" : Text(offset + I(offset + 8));
            var blends = I(offset + 56);
            Range(offset + I(offset + 60), (long)blends * 2);
            var indices = new short[blends];
            for (var blend = 0; blend < blends; blend++) indices[blend] = S(offset + I(offset + 60) + blend * 2);
            sequences.Add((index, offset, label, activity, KindOf(activity, label), indices));
        }
        foreach (var sequence in sequences.Where(sequence => sequence.Kind is not null && sequence.Animations.Length > 0))
        {
            var (index, offset, label, activity, kind, animations) = sequence;
            var animationIndex = animations[0];
            var animation = Anim(animationIndex);
            Range(offset + I(offset + 28), (long)I(offset + 24) * 80);
            var events = new double[I(offset + 24)];
            for (var eventIndex = 0; eventIndex < events.Length; eventIndex++)
            {
                var cycle = F(offset + I(offset + 28) + 80 * eventIndex);
                if (!float.IsFinite(cycle) || cycle is < 0 or > 1) throw new InvalidDataException("动画事件时点无效。");
                events[eventIndex] = cycle * Math.Max(0, animation.Frames - 1);
            }
            string? unsupported = null;
            try
            {
                if (animations.Length != 1) throw new NotSupportedException("多动画混合的序列暂不支持。");
                if (animation.Frames < 2 || animation.Fps == 0) throw new NotSupportedException("此占位动作没有可裁剪的有效帧。");
                if (sequences.Any(other => other.Animations.Contains(animationIndex)
                    && (other.Kind != kind || other.Animations.Length != 1)))
                    throw new NotSupportedException("此动画与其他动作或混合序列共享，暂不裁剪。");
                if ((I(offset + 12) & 1) != 0) throw new NotSupportedException("循环序列暂不支持。");
                if (I(offset + 148) != 0 || F(offset + 132) != 0) throw new NotSupportedException("此附加动画层 / 结束帧格式暂不支持。");
                if (I(animation.Offset + 20) != 0 || I(animation.Offset + 72) != 0) throw new NotSupportedException("带根运动或层级重定向的动画暂不支持。");
                if (animation.SectionSize < 0) throw new InvalidDataException("分段动画长度无效。");
                if (animation.SectionSize > 0)
                {
                    var sections = animation.Frames / animation.SectionSize + 2;
                    Range(animation.Offset + animation.SectionOffset, (long)sections * 8);
                    for (var section = 0; section < sections; section++)
                        if (I(animation.Offset + animation.SectionOffset + 8 * section) != 0)
                            throw new NotSupportedException("外置 .ani 动画块暂不支持。");
                }
                else if (I(animation.Offset + 52) != 0) throw new NotSupportedException("外置 .ani 动画块暂不支持。");
                _ = Frame(animationIndex, 0);
                _ = Frame(animationIndex, animation.Frames - 1);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException) { unsupported = ex.Message; }
            clips.Add(new(index, string.IsNullOrWhiteSpace(label) ? activity : label, animationIndex, animation.Frames, animation.Fps, events, unsupported,
                kind!.Value));
        }
        return clips;
    }

    private static WeaponAnimationKind? KindOf(string activity, string label)
    {
        if (activity.Contains("RELOAD", StringComparison.OrdinalIgnoreCase) || label.Contains("reload", StringComparison.OrdinalIgnoreCase)) return WeaponAnimationKind.Reload;
        if (activity.StartsWith("ACT_VM_DRAW", StringComparison.OrdinalIgnoreCase) || activity.Equals("ACT_VM_DEPLOY", StringComparison.OrdinalIgnoreCase)
            || (activity.Length == 0 || activity.Equals("ACT_INVALID", StringComparison.OrdinalIgnoreCase))
                && (label.Equals("draw", StringComparison.OrdinalIgnoreCase) || label.StartsWith("draw_", StringComparison.OrdinalIgnoreCase)
                    || label.Equals("deploy", StringComparison.OrdinalIgnoreCase) || label.StartsWith("deploy_", StringComparison.OrdinalIgnoreCase))) return WeaponAnimationKind.Draw;
        return null;
    }

    private short Value(int pointer, int frame)
    {
        while (true)
        {
            Range(pointer, 2);
            int valid = _data[pointer], total = _data[pointer + 1];
            if (valid == 0 || valid > total) throw new InvalidDataException("动画压缩帧数据无效。");
            if (frame < total) return S(pointer + 2 * (Math.Min(frame, valid - 1) + 1));
            frame -= total;
            pointer = checked(pointer + 2 * (valid + 1));
        }
    }

    // Canonical single-frame records also let us verify every retained frame without rendering.
    internal byte[] Frame(int index, int frame)
    {
        var animation = Anim(index);
        if ((uint)frame >= animation.Frames) throw new InvalidDataException("动画帧编号无效。");
        var offset = animation.Offset;
        var block = I(offset + 52);
        var pointer = I(offset + 56);
        if (animation.SectionSize > 0)
        {
            int section;
            if (animation.Frames > animation.SectionSize && frame == animation.Frames - 1)
            { section = animation.Frames / animation.SectionSize + 1; frame = 0; }
            else { section = frame / animation.SectionSize; frame %= animation.SectionSize; }
            block = I(offset + animation.SectionOffset + section * 8);
            pointer = I(offset + animation.SectionOffset + section * 8 + 4);
        }
        if (block != 0) throw new NotSupportedException("外置动画块暂不支持。");
        var position = checked(offset + pointer);
        using var output = new MemoryStream();
        for (var recordIndex = 0; recordIndex <= I(156); recordIndex++)
        {
            Range(position, 4);
            int bone = _data[position], flags = _data[position + 1], next = S(position + 2);
            if (bone == 255 && flags == 0 && next == 0)
            { output.Write([255, 0, 0, 0]); return output.ToArray(); }
            if (bone >= I(156) || (flags & ~0x3F) != 0 || next < 0) throw new InvalidDataException("骨骼动画记录无效。");
            var payload = position + 4;
            using var record = new MemoryStream();
            using var writer = new BinaryWriter(record, Encoding.UTF8, leaveOpen: true);
            writer.Write((byte)bone); writer.Write((byte)flags); writer.Write((short)0);
            if ((flags & 0x23) != 0)
            {
                if ((flags & 0x0C) != 0) throw new NotSupportedException("此骨骼压缩组合暂不支持。");
                var size = ((flags & 0x20) != 0 ? 8 : (flags & 2) != 0 ? 6 : 0) + ((flags & 1) != 0 ? 6 : 0);
                Range(payload, size); writer.Write(_data, payload, size);
            }
            else
            {
                var groups = ((flags & 8) != 0 ? 1 : 0) + ((flags & 4) != 0 ? 1 : 0);
                writer.Write(new byte[groups * 6]);
                for (var group = 0; group < groups; group++)
                for (var axis = 0; axis < 3; axis++)
                {
                    var relative = S(payload + group * 6 + axis * 2);
                    if (relative == 0) continue;
                    if (relative < 0) throw new InvalidDataException("动画通道指针无效。");
                    var value = Value(payload + group * 6 + relative, frame);
                    var end = record.Length;
                    record.Position = 4 + group * 6 + axis * 2;
                    writer.Write(checked((short)(end - (4 + group * 6))));
                    record.Position = end;
                    writer.Write((byte)1); writer.Write((byte)1); writer.Write(value);
                }
            }
            if (next != 0) { record.Position = 2; writer.Write(checked((short)record.Length)); }
            output.Write(record.ToArray());
            if (next == 0) return output.ToArray();
            position = checked(position + next);
        }
        throw new InvalidDataException("骨骼动画链无法结束。");
    }

    internal byte[] Trim(IReadOnlyList<Clip> clips, IReadOnlyDictionary<int, int> frames, CancellationToken token)
    {
        using var output = new MemoryStream(); output.Write(_data);
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        void PutI(int offset, int value) { output.Position = offset; writer.Write(value); }
        void PutF(int offset, float value) { output.Position = offset; writer.Write(value); }
        foreach (var (index, newFrames) in frames)
        {
            token.ThrowIfCancellationRequested();
            var animation = Anim(index);
            if (newFrames < 2 || newFrames >= animation.Frames) throw new InvalidDataException("裁尾后必须保留至少两帧。");
            if (animation.SectionSize > 0 && newFrames > animation.SectionSize)
            {
                output.Position = output.Length;
                while (output.Length % 4 != 0) output.WriteByte(0);
                var frameOffset = checked((int)output.Length);
                output.Write(Frame(index, newFrames - 1));
                var section = newFrames / animation.SectionSize + 1;
                PutI(animation.Offset + animation.SectionOffset + section * 8, 0);
                PutI(animation.Offset + animation.SectionOffset + section * 8 + 4, frameOffset - animation.Offset);
            }
            PutI(animation.Offset + 16, newFrames);
            var ratio = (double)(animation.Frames - 1) / (newFrames - 1);
            var rules = I(animation.Offset + 60);
            var ruleOffset = animation.Offset + I(animation.Offset + 64);
            Range(ruleOffset, (long)rules * 152);
            for (var rule = 0; rule < rules; rule++)
            foreach (var field in new[] { 76, 80, 84, 88, 96 })
            {
                var location = ruleOffset + rule * 152 + field;
                var value = F(location);
                if (!float.IsFinite(value)) throw new InvalidDataException("IK 相位无效。");
                if (value >= 0) PutF(location, (float)(value * ratio));
            }
        }
        foreach (var clip in clips.Where(clip => frames.ContainsKey(clip.Animation)))
        {
            var offset = I(192) + clip.Sequence * 212;
            var events = offset + I(offset + 28);
            var kept = 0;
            var newEnd = frames[clip.Animation] - 1;
            for (var eventIndex = 0; eventIndex < clip.EventFrames.Length; eventIndex++)
            {
                if (clip.EventFrames[eventIndex] > newEnd + 0.00001) continue;
                var oldOffset = events + eventIndex * 80;
                var newOffset = events + kept++ * 80;
                output.Position = newOffset; output.Write(_data, oldOffset, 80);
                PutF(newOffset, (float)(clip.EventFrames[eventIndex] / newEnd));
                if (I(oldOffset + 76) != 0) PutI(newOffset + 76, checked(oldOffset + I(oldOffset + 76) - newOffset));
            }
            PutI(offset + 24, kept);
        }
        PutI(76, checked((int)output.Length));
        var result = output.ToArray();
        var modified = new StudioReloadModel(result);
        _ = modified.Inspect();
        foreach (var (index, newFrames) in frames)
        for (var frame = 0; frame < newFrames; frame++)
        {
            token.ThrowIfCancellationRequested();
            if (!Frame(index, frame).AsSpan().SequenceEqual(modified.Frame(index, frame)))
                throw new InvalidDataException("裁尾后的保留帧校验不一致，未生成副本。");
        }
        return result;
    }
}
