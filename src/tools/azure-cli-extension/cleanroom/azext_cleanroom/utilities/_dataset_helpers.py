from cleanroom_common.azure_cleanroom_core.models.dataset import DataAccessPolicy


def load_access_policy_from_file(policy_file: str) -> DataAccessPolicy:
    """
    Load a DataAccessPolicy from a JSON or YAML file.

    Args:
        policy_file (str): Path to the policy file

    Returns:
        DataAccessPolicy: The loaded access policy object

    Raises:
        CLIError: If the file cannot be found or parsed
    """
    import json
    import os

    import yaml
    from azure.cli.core.util import CLIError

    if not os.path.exists(policy_file):
        raise CLIError(f"Access policy file '{policy_file}' not found.")

    try:
        with open(policy_file, "r") as f:
            if policy_file.endswith((".yaml", ".yml")):
                data = yaml.safe_load(f)
            else:
                data = json.load(f)

        # Convert the loaded data to DataAccessPolicy
        if isinstance(data, dict):
            # Handle direct policy format
            if "accessMode" in data and "allowedFields" in data:
                return DataAccessPolicy(**data)
            else:
                # Handle wrapped policy format
                if "datasetAccessPolicy" in data:
                    return DataAccessPolicy(**data["datasetAccessPolicy"])
                else:
                    raise CLIError(
                        "Invalid access policy file format. Expected 'accessMode' and 'allowedFields' properties."
                    )
        else:
            raise CLIError("Access policy file must contain a JSON object.")

    except yaml.YAMLError as e:
        raise CLIError(f"Failed to parse YAML access policy file: {e}")
    except json.JSONDecodeError as e:
        raise CLIError(f"Failed to parse JSON access policy file: {e}")
    except Exception as e:
        raise CLIError(f"Failed to load access policy from file: {e}")


def generate_access_policy_from_fields(
    access_mode: str, allowed_fields: list[str]
) -> DataAccessPolicy:
    """
    Generate a DataAccessPolicy from provided field definitions.

    Args:
        access_mode (str): The access mode (read, write)
        allowed_fields (list): List of field names

    Returns:
        DataAccessPolicy: The generated access policy object

    Raises:
        CLIError: If the parameters are invalid
    """
    from azure.cli.core.util import CLIError
    from cleanroom_common.azure_cleanroom_core.models.dataset import AccessMode

    if not access_mode:
        raise CLIError(
            "Access mode must be specified when generating access policy from fields."
        )

    if not allowed_fields:
        raise CLIError(
            "Allowed fields must be specified when generating access policy from fields."
        )

    # Validate and convert access mode
    try:
        data_access_mode = AccessMode(access_mode.strip().lower())
    except ValueError:
        valid_modes = [mode.value for mode in AccessMode]
        raise CLIError(
            f"Invalid access mode '{access_mode}'. Valid modes are: {', '.join(valid_modes)}"
        )

    return DataAccessPolicy(accessMode=data_access_mode, allowedFields=allowed_fields)
