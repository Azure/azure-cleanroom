from knack.log import get_logger

logger = get_logger(__name__)


def az_cli(args_str: str):
    import os

    from azure.cli.core import get_default_cli
    from knack.util import CommandResultItem

    args = args_str.split()
    cli = get_default_cli()
    out_file = open(os.devnull, "w")
    try:
        cli.invoke(args, out_file=out_file)
    except SystemExit:
        pass
    except:
        logger.warning(f"Command failed: {args}")
        raise

    if isinstance(cli.result, CommandResultItem):
        if cli.result.result:
            return cli.result.result
        elif cli.result.error:
            if isinstance(cli.result.error, SystemExit):
                if cli.result.error.code == 0:
                    return
            logger.warning(f"Command failed: {args}, {cli.result.error}")
            raise cli.result.error
