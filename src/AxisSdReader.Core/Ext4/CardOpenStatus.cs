namespace AxisSdReader.Core.Ext4;

/// <summary>Outcome of attempting to open an SD card (device or image) as an Axis ext4 card.</summary>
public enum CardOpenStatus
{
    /// <summary>An ext4 filesystem was found and opened read-only.</summary>
    Ok,

    /// <summary>The disk was readable but no ext4 filesystem was found on any partition.</summary>
    NoExt4FileSystem,

    /// <summary>A LUKS encryption header was found but this reader cannot handle it (e.g. LUKS2), so
    /// the card cannot be read offline.</summary>
    Encrypted,

    /// <summary>The card is LUKS-encrypted and needs a passphrase to unlock — supply one and retry.</summary>
    EncryptedNeedsPassphrase,

    /// <summary>A passphrase was supplied but did not unlock the card.</summary>
    IncorrectPassphrase,

    /// <summary>An ext4 superblock was found but it uses features the reader does not support.</summary>
    IncompatibleExt4,
}
