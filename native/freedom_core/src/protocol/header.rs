use crc32fast::Hasher;
use std::convert::TryFrom;

// Protocol Constants
pub const HEADER_SIZE: usize = 16;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum MessageType {
    Handshake = 0x01,
    Onion = 0x02,
    DhtFindNode = 0x03,
    DhtFindNodeRes = 0x04,
    Store = 0x05,
    StoreRes = 0x06,
    Fetch = 0x07,
    FetchRes = 0x08,
    Put = 0x0A,
    GetValueReq = 0x0B,
    GetValueRes = 0x0C,
    // Add a fallback for unknown types to handle forward compatibility safely
    Unknown = 0xFF,
}

impl From<u8> for MessageType {
    fn from(value: u8) -> Self {
        match value {
            0x01 => MessageType::Handshake,
            0x02 => MessageType::Onion,
            0x03 => MessageType::DhtFindNode,
            0x04 => MessageType::DhtFindNodeRes,
            0x05 => MessageType::Store,
            0x06 => MessageType::StoreRes,
            0x07 => MessageType::Fetch,
            0x08 => MessageType::FetchRes,
            0x0A => MessageType::Put,
            0x0B => MessageType::GetValueReq,
            0x0C => MessageType::GetValueRes,
            _ => MessageType::Unknown,
        }
    }
}

#[derive(Debug, Clone)]
pub struct FixedHeader {
    pub version: u8,
    pub flags: u8,
    pub message_type: MessageType,
    pub reserved: u8,
    pub request_id: u32,
    pub payload_length: u32,
    pub checksum: u32, // CRC32
}

#[derive(Debug, thiserror::Error)]
pub enum HeaderError {
    #[error("Buffer too small to contain header")]
    BufferTooSmall,
    #[error("Checksum mismatch: expected {expected:#x}, got {computed:#x}")]
    ChecksumMismatch { expected: u32, computed: u32 },
}

impl FixedHeader {
    /// Creates a new header and automatically calculates the checksum for the given payload.
    pub fn new(
        version: u8,
        flags: u8,
        message_type: MessageType,
        request_id: u32,
        payload_length: u32,
        checksum: u32,
    ) -> Self {
        Self {
            version,
            flags,
            message_type,
            reserved: 0,
            request_id,
            payload_length,
            checksum,
        }
    }

    pub fn create(
        message_type: MessageType,
        request_id: u32,
        payload: &[u8],
    ) -> Self {
        let mut hasher = Hasher::new();
        hasher.update(payload);
        let checksum = hasher.finalize();

        Self::new(
            1, // version
            0, // flags
            message_type,
            request_id,
            payload.len() as u32,
            checksum,
        )
    }

    /// Serializes the header into a [u8; 16] array in Big-Endian.
    pub fn to_bytes(&self) -> [u8; HEADER_SIZE] {
        let mut bytes = [0u8; HEADER_SIZE];
        
        bytes[0] = self.version;
        bytes[1] = self.flags;
        bytes[2] = self.message_type as u8;
        bytes[3] = self.reserved;
        
        // Big-Endian serialization for multi-byte integers
        bytes[4..8].copy_from_slice(&self.request_id.to_be_bytes());
        bytes[8..12].copy_from_slice(&self.payload_length.to_be_bytes());
        bytes[12..16].copy_from_slice(&self.checksum.to_be_bytes());
        
        bytes
    }

    /// Parses a header from a byte slice.
    pub fn from_bytes(bytes: &[u8]) -> Result<Self, HeaderError> {
        if bytes.len() < HEADER_SIZE {
            return Err(HeaderError::BufferTooSmall);
        }

        let version = bytes[0];
        let flags = bytes[1];
        let message_type = MessageType::from(bytes[2]);
        let reserved = bytes[3];

        let request_id = u32::from_be_bytes(bytes[4..8].try_into().unwrap());
        let payload_length = u32::from_be_bytes(bytes[8..12].try_into().unwrap());
        let checksum = u32::from_be_bytes(bytes[12..16].try_into().unwrap());

        Ok(Self {
            version,
            flags,
            message_type,
            reserved,
            request_id,
            payload_length,
            checksum,
        })
    }
}