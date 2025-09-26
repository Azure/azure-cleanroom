from azext_cleanroom._help import helps  # pylint: disable=unused-import
from azure.cli.core import AzCommandsLoader


class CleanRoomCommandsLoader(AzCommandsLoader):

    def __init__(self, cli_ctx=None):
        from azure.cli.core.commands import CliCommandType

        cleanroom_custom = CliCommandType(operations_tmpl="azext_cleanroom.custom#{}")
        super().__init__(cli_ctx=cli_ctx, custom_command_type=cleanroom_custom)

    def load_command_table(self, args):
        from azext_cleanroom.commands import load_command_table

        load_command_table(self, args)
        return self.command_table

    def load_arguments(self, command):
        from azext_cleanroom._params import load_arguments

        load_arguments(self, command)


COMMAND_LOADER_CLS = CleanRoomCommandsLoader
