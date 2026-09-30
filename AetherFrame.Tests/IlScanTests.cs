using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The IL walk behind the boundary test that reads constructor calls: it finds a constructor call
/// written as a target-typed <c>new</c>, which a source scan would miss, and walks every method of
/// a real assembly to its end without meeting an opcode it doesn't know.
/// </summary>
public class IlScanTests
{
    [Fact]
    public void FindsATargetTypedConstructorCall_AndWalksEveryMethodToItsEnd()
    {
        Assert.Equal(16, TargetTypedConstruction().Capacity);

        using var pe = new PEReader(File.OpenRead(typeof(IlScanTests).Assembly.Location));
        var metadata = pe.GetMetadataReader();
        var walked = 0;
        var found = false;
        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            var operands = IlScan.TokenOperands(pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader());
            walked++;
            if (metadata.GetString(method.Name) != nameof(TargetTypedConstruction))
            {
                continue;
            }

            foreach (var (opCode, token) in operands)
            {
                var entity = MetadataTokens.EntityHandle(token);
                if (opCode != ILOpCode.Newobj || entity.Kind != HandleKind.MemberReference)
                {
                    continue;
                }

                var member = metadata.GetMemberReference((MemberReferenceHandle)entity);
                if (member.Parent.Kind == HandleKind.TypeReference)
                {
                    var parent = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
                    found |= metadata.GetString(member.Name) == ".ctor" && metadata.GetString(parent.Name) == nameof(StringBuilder);
                }
            }
        }

        Assert.True(walked > 100, $"only {walked} method bodies were walked");
        Assert.True(found, "the target-typed constructor call was not found");
    }

    private static StringBuilder TargetTypedConstruction()
    {
        StringBuilder made = new(16);
        return made;
    }
}
