const sampleMessageCallback = async ({ context, message, say, logger }) => {
  try {
    const greeting = context.matches[0].toLowerCase();
    await say(`Hey there <@${message.user}>! I heard "${greeting}".`);
  } catch (error) {
    logger.error(error);
  }
};

export { sampleMessageCallback };
