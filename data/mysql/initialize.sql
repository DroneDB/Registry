
CREATE USER 'registry'@'localhost' IDENTIFIED BY 'YLepR7DgjfzFw25C';
GRANT USAGE ON *.* TO 'registry'@'localhost';
FLUSH PRIVILEGES;

CREATE DATABASE `RegistryData` /*!40100 COLLATE 'latin1_general_ci' */;
CREATE DATABASE `RegistryAuth` /*!40100 COLLATE 'latin1_general_ci' */;

GRANT ALL ON `registryauth`.* TO 'registry'@'localhost' WITH GRANT OPTION;
GRANT ALL ON `registrydata`.* TO 'registry'@'localhost' WITH GRANT OPTION;
FLUSH PRIVILEGES;