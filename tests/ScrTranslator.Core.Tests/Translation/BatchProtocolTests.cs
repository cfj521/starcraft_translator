using ScrTranslator.Core.Translation.Providers;
namespace ScrTranslator.Core.Tests.Translation;
public class BatchProtocolTests
{
    [Fact] public void Build_NumbersLines()
    { var u = BatchProtocol.BuildUser(new[]{"가","나"}); Assert.Contains("[1]", u); Assert.Contains("[2]", u); Assert.Contains("가", u); }
    [Fact] public void Parse_Good()
    { var r = BatchProtocol.Parse("[1] 甲\n[2] 乙", 2); Assert.Equal(new[]{"甲","乙"}, r); }
    [Fact] public void Parse_WrongCount_Null()
    { Assert.Null(BatchProtocol.Parse("[1] 甲", 2)); }
    [Fact] public void Parse_MissingIndex_Null()
    { Assert.Null(BatchProtocol.Parse("[1] 甲\n[3] 丙", 2)); }
    [Fact] public void Parse_DuplicateIndex_Null()
    { Assert.Null(BatchProtocol.Parse("[1] 甲\n[2] 乙\n[1] 甲2", 2)); }
}
