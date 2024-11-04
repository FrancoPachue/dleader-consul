using Xunit;
using System;
using DLeader.Consul.Exceptions;

namespace DLeader.Consul.Tests.Exceptions
{
    public class LeadershipExceptionTests
    {
        [Fact]
        public void Constructor_WithMessage_SetsMessageProperty()
        {
            // Arrange
            string expectedMessage = "Test leadership error";

            // Act
            var exception = new LeadershipException(expectedMessage);

            // Assert
            Assert.Equal(expectedMessage, exception.Message);
        }

        [Fact]
        public void Constructor_WithMessageAndInnerException_SetsBothProperties()
        {
            // Arrange
            string expectedMessage = "Test leadership error";
            var innerException = new Exception("Inner exception message");

            // Act
            var exception = new LeadershipException(expectedMessage, innerException);

            // Assert
            Assert.Equal(expectedMessage, exception.Message);
            Assert.Same(innerException, exception.InnerException);
        }

        [Fact]
        public void LeadershipException_InheritsFromConsulException()
        {
            // Arrange & Act
            var exception = new LeadershipException("Test message");

            // Assert
            Assert.IsAssignableFrom<ConsulException>(exception);
        }

        [Fact]
        public void LeadershipException_CanBeCaughtAsConsulException()
        {
            // Arrange
            Exception? caughtException = null;

            // Act
            try
            {
                throw new LeadershipException("Test exception");
            }
            catch (ConsulException ex)
            {
                caughtException = ex;
            }

            // Assert
            Assert.NotNull(caughtException);
            Assert.IsType<LeadershipException>(caughtException);
        }
    }
}