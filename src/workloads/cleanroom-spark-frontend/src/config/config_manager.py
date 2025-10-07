import yaml

from .configuration import Configuration


class ConfigManager(object):
    """
    This class manages the configuration of the web server.
    It loads the configuration from a file and provides methods to access it.
    """

    config_file: str = ""
    config: Configuration

    def __init__(self, config_file):
        """
        Initializes the ConfigManager with the given configuration file.

        :param config_file: The path to the configuration file.
        """
        self.config_file = config_file
        self.config = self.load_config()

    def load_config(self) -> Configuration:
        """
        Loads the configuration from the file.

        :return: The loaded configuration.
        """
        config_data = {}
        with open(self.config_file, "r") as file:
            config_data = yaml.safe_load(file)
        return Configuration(**config_data)

    def get_config(self):
        """
        Returns the loaded configuration.

        :return: The loaded configuration.
        """
        return self.config
