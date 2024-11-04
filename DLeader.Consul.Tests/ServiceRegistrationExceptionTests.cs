using Xunit;
using System;
using DLeader.Consul.Exceptions;

namespace DLeader.Consul.Tests.Exceptions
{
    public class ServiceRegistrationExceptionTests
    {
        [Fact]
        public void Constructor_WithMessage_SetsMessageProperty()
        {
            // Arrange
            string expectedMessage = "Test service registration error";

            // Act
            var exception = new ServiceRegistrationException(expectedMessage);

            // Assert
            Assert.Equal(expectedMessage, exception.Message);
        }

        [Fact]
        public void Constructor_WithMessageAndInnerException_SetsBothProperties()
        {
            // Arrange
            string expectedMessage = "Test service registration error";
            var innerException = new Exception("Inner exception message");

            // Act
            var exception = new ServiceRegistrationException(expectedMessage, innerException);

            // Assert
            Assert.Equal(expectedMessage, exception.Message);
            Assert.Same(innerException, exception.InnerException);
        }

        [Fact]
        public void ServiceRegistrationException_InheritsFromConsulException()
        {
            // Arrange & Act
            var exception = new ServiceRegistrationException("Test message");

            // Assert
            Assert.IsAssignableFrom<ConsulException>(exception);
        }

        [Fact]
        public void ServiceRegistrationException_CanBeCaughtAsConsulException()
        {
            // Arrange
            Exception? caughtException = null;

            // Act
            try
            {
                throw new ServiceRegistrationException("Test exception");
            }
            catch (ConsulException ex)
            {
                caughtException = ex;
            }

            // Assert
            Assert.NotNull(caughtException);
            Assert.IsType<ServiceRegistrationException>(caughtException);
        }
    }
}