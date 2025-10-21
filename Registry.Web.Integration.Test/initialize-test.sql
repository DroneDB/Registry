-- Create test databases
CREATE DATABASE IF NOT EXISTS RegistryAuthTest CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE IF NOT EXISTS RegistryDataTest CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE DATABASE IF NOT EXISTS RegistryHangfireTest CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

-- Create test user
CREATE USER IF NOT EXISTS 'registry'@'%' IDENTIFIED BY 'testpassword';
GRANT ALL PRIVILEGES ON RegistryAuthTest.* TO 'registry'@'%';
GRANT ALL PRIVILEGES ON RegistryDataTest.* TO 'registry'@'%';
GRANT ALL PRIVILEGES ON RegistryHangfireTest.* TO 'registry'@'%';
FLUSH PRIVILEGES;
