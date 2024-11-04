using Xunit;
using System;
using DLeader.Consul.Exceptions;

namespace DLeader.Consul.Tests.Exceptions
{
    public class ConsulExceptionTests
    {
        [Fact]
        public void Constructor_WithMessage_SetsMessageProperty()
        {
            // Arrange
            string expectedMessage = "Test consul error";

            // Act
            var exception = new ConsulException(expectedMessage);

            // Assert
            Assert.Equal(expectedMessage, exception.Message);
        }

        [Fact]
        public void Constructor_WithMessageAndInnerException_SetsBothProperties()
        {
            // Arrange
            string expectedMessage = "Test consul error";
            var innerException = new Exception("Inner exception");

            // Act
            var exception = new ConsulException(expectedMessage, innerException);

            // Assert
            Assert.Equal(expectedMessage, exception.Message);
            Assert.Same(innerException, exception.InnerException);
        }

        [Fact]
        public void ConsulException_InheritsFromException()
        {
            // Arrange & Act
            var exception = new ConsulException("Test");

            // Assert
            Assert.IsAssignableFrom<Exception>(exception);
        }
    }
}