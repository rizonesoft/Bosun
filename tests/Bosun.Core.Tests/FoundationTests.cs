using Xunit;

namespace Bosun.Core.Tests;

public class FoundationTests
{
    [Fact]
    public void Foundation_names_the_product() => Assert.Equal("Bosun", Foundation.Product);
}
