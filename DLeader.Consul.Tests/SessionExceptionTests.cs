using Xunit;
using System;
using DLeader.Consul.Exceptions;

namespace DLeader.Consul.Tests.Exceptions
{
    public class SessionExceptionTests
    {
        [Fact]
        public void Constructor_WithMessage_SetsMessageProperty()
        {
            // Arrange
            string expectedMessage = "Test session error";

            // Act
            var exception = new SessionException(expectedMessage);

            // Assert
            Assert.Equal(expectedMessage, exception.Message);
        }

        [Fact]
        public void Constructor_WithMessageAndInnerException_SetsBothProperties()
        {
            // Arrange
            string expectedMessage = "Test session error";
            var innerException = new Exception("Inner exception");

            // Act
            var exception = new SessionException(expectedMessage, innerException);

            // Assert
            Assert.Equal(expectedMessage, exception.Message);
            Assert.Same(innerException, exception.InnerException);
        }

        [Fact]
        public void SessionException_InheritsFromConsulException()
        {
            // Arrange & Act
            var exception = new SessionException("Test");

            // Assert
            Assert.IsAssignableFrom<ConsulException>(exception);
        }
    }
}