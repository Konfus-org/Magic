using Magic.Services;
using Magic.Utils;
using Xunit;

namespace Magic.IntegrationTests;

/// <summary>
/// The real file system on real files, over a temp folder: what the unit suite's path tests leave out.
/// </summary>
public sealed class FileSystemTests : IDisposable
{
    private readonly TempFolder _root = new();
    private readonly FileSystem _files = new();

    public void Dispose()
    {
        _root.Dispose();
    }

    [Fact]
    public void A_text_file_reads_back_as_written()
    {
        string path = _root.Write("note.txt", "hello");

        Result<string> read = _files.ReadText(path);

        Assert.Equal("hello", read.Payload);
    }

    [Fact]
    public void A_text_file_with_a_byte_order_mark_reads_back_without_it()
    {
        string path = _root.Write("note.txt", "");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i']);

        Result<string> read = _files.ReadText(path);

        Assert.Equal("hi", read.Payload);
    }

    [Fact]
    public async Task A_text_file_bigger_than_the_read_buffer_reads_back_whole()
    {
        string text = new('x', 200 * 1024);
        string path = _root.Write("big.txt", text);

        Result<string> read = await _files.ReadTextAsync(path);

        Assert.Equal(text, read.Payload);
    }

    [Fact]
    public void A_file_is_parsed_from_its_bytes_without_a_byte_order_mark()
    {
        string path = _root.Write("note.txt", "");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i']);

        Result<int> read = _files.Read(path, static bytes => bytes.Length);

        Assert.Equal(2, read.Payload);
    }

    [Fact]
    public async Task A_file_bigger_than_the_read_buffer_is_parsed_whole()
    {
        string path = _root.Write("big.txt", new string('x', 200 * 1024));

        Result<int> read = await _files.ReadAsync(path, static bytes => bytes.Length);

        Assert.Equal(200 * 1024, read.Payload);
    }

    [Fact]
    public void Parsing_a_missing_file_fails()
    {
        Result<int> read = _files.Read(Path.Combine(_root.Path, "missing.txt"), static bytes => bytes.Length);

        Assert.True(read.Failed);
    }

    [Fact]
    public async Task The_hash_of_files_is_the_same_for_the_same_contents()
    {
        string first = _root.Write("a.txt", "one");
        string second = _root.Write("b.txt", "one");

        Result<string> hashA = await _files.HashAsync([first]);
        Result<string> hashB = await _files.HashAsync([second]);

        Assert.Equal(hashA.Payload, hashB.Payload);
    }

    [Fact]
    public async Task The_hash_of_files_changes_with_their_contents()
    {
        string first = _root.Write("a.txt", "one");
        string second = _root.Write("b.txt", "two");

        Result<string> hashA = await _files.HashAsync([first]);
        Result<string> hashB = await _files.HashAsync([second]);

        Assert.NotEqual(hashA.Payload, hashB.Payload);
    }

    [Fact]
    public async Task The_hash_of_files_depends_on_their_order()
    {
        string first = _root.Write("a.txt", "one");
        string second = _root.Write("b.txt", "two");

        Result<string> forward = await _files.HashAsync([first, second]);
        Result<string> backward = await _files.HashAsync([second, first]);

        Assert.NotEqual(forward.Payload, backward.Payload);
    }

    [Fact]
    public async Task Hashing_a_missing_file_fails()
    {
        Result<string> hash = await _files.HashAsync([Path.Combine(_root.Path, "missing.txt")]);

        Assert.True(hash.Failed);
    }
}
